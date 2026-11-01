// SPDX-License-Identifier: MIT
#import <Foundation/Foundation.h>
#import <Security/Security.h>
#import <SystemConfiguration/SystemConfiguration.h>
#include "egress_mac.h"
#include "process_identity.h"
#include <arpa/inet.h>
#include <errno.h>
#include <ifaddrs.h>
#include <libproc.h>
#include <net/if.h>
#include <net/route.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/sysctl.h>
#include <sys/un.h>
#include <unistd.h>

static int denied(void) { const char *mock = getenv("EGRESS_MOCK_ONLY"); return mock != NULL && strcmp(mock, "1") == 0; }
static int root_allowed(void) { return !denied() && geteuid() == 0; }
static int32_t json_copy(id object, uint8_t *output, size_t capacity, size_t *required)
{
    if (required == NULL) return EGRESS_MAC_INVALID;
    *required = 0;
    if (output == NULL && capacity != 0) return EGRESS_MAC_INVALID;
    NSData *data = [NSJSONSerialization dataWithJSONObject:object options:NSJSONWritingSortedKeys error:nil];
    if (data == nil) return EGRESS_MAC_INTERNAL;
    *required = data.length;
    if (output == NULL || capacity < data.length) return EGRESS_MAC_BUFFER_SMALL;
    memcpy(output, data.bytes, data.length); return EGRESS_MAC_OK;
}
static NSDictionary *process_info(pid_t pid)
{
    struct egress_process_identity info;
    if (egress_read_process_identity(pid, sysctl, proc_pidpath, &info) != 0) return nil;
    return @{ @"UserId":@(info.uid), @"GroupId":@(info.gid), @"ProcessId":@(info.pid),
        @"StartedAtMicroseconds":@(info.started_at), @"ExecutablePath":[NSString stringWithUTF8String:info.path] };
}
uint32_t egress_mac_uid(void) { return getuid(); }
uint32_t egress_mac_gid(void) { return getgid(); }
uint32_t egress_mac_euid(void) { return geteuid(); }
int32_t egress_mac_copy_process_json(int32_t pid, uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { errno = 0; if (denied()) return EGRESS_MAC_UNAVAILABLE; NSDictionary *info = process_info(pid);
        return info == nil ? EGRESS_MAC_UNAVAILABLE : json_copy(info, output, capacity, required); }
}
int32_t egress_mac_copy_peer_json(int32_t descriptor, uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool {
        errno = 0; if (denied()) return EGRESS_MAC_UNAVAILABLE;
        uid_t uid; gid_t gid; pid_t pid = 0; socklen_t length = sizeof(pid);
        if (getpeereid(descriptor, &uid, &gid) != 0 || getsockopt(descriptor, SOL_LOCAL, LOCAL_PEERPID, &pid, &length) != 0) return EGRESS_MAC_IO;
        if (length != sizeof(pid) || pid <= 0) { errno = EINVAL; return EGRESS_MAC_UNAVAILABLE; }
        NSDictionary *info = process_info(pid);
        if (info == nil) return EGRESS_MAC_UNAVAILABLE;
        if ([info[@"UserId"] unsignedIntValue] != uid) { errno = EPERM; return EGRESS_MAC_UNAVAILABLE; }
        NSMutableDictionary *peer = [info mutableCopy]; peer[@"GroupId"] = @(gid);
        return json_copy(peer, output, capacity, required);
    }
}
static NSDictionary *stat_info(const struct stat *value)
{
    return @{ @"UserId":@(value->st_uid), @"GroupId":@(value->st_gid), @"Mode":@(value->st_mode & 07777),
        // S_IS* are C int expressions. Boxing them directly emits JSON 0/1, which
        // the AOT-generated managed bool contract correctly refuses to deserialize.
        @"IsRegular":S_ISREG(value->st_mode) ? @YES : @NO, @"IsDirectory":S_ISDIR(value->st_mode) ? @YES : @NO,
        @"IsSocket":S_ISSOCK(value->st_mode) ? @YES : @NO, @"IsSymbolicLink":S_ISLNK(value->st_mode) ? @YES : @NO, @"Size":@(value->st_size) };
}
int32_t egress_mac_file_info_fixture(uint32_t mode, uint8_t *output, size_t capacity, size_t *required)
{
    // Pure wire-contract probe: supplied mode only, no filesystem/identity access.
    @autoreleasepool { struct stat value = {0}; value.st_mode = (mode_t)mode;
        return json_copy(stat_info(&value), output, capacity, required); }
}
int32_t egress_mac_copy_file_info(const char *path, uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { errno = 0; struct stat value;
        if (denied()) return EGRESS_MAC_UNAVAILABLE;
        if (!egress_mac_path_is_absolute(path) || lstat(path, &value) != 0) return EGRESS_MAC_IO;
        return json_copy(stat_info(&value), output, capacity, required); }
}
int32_t egress_mac_copy_fd_info(int32_t descriptor, uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { errno = 0; struct stat value;
        if (denied()) return EGRESS_MAC_UNAVAILABLE;
        if (fstat(descriptor, &value) != 0) return EGRESS_MAC_IO;
        return json_copy(stat_info(&value), output, capacity, required); }
}
int32_t egress_mac_verify_process_code(int32_t pid, const char *expected_path)
{
    @autoreleasepool {
        if (denied() || !egress_mac_path_is_absolute(expected_path)) return EGRESS_MAC_UNAVAILABLE;
        SecCodeRef guest = NULL; SecStaticCodeRef actual = NULL, expected = NULL;
        CFDictionaryRef actualInfo = NULL, expectedInfo = NULL;
        NSDictionary *attributes = @{ (__bridge NSString *)kSecGuestAttributePid:@(pid) };
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:expected_path]];
        BOOL valid = SecCodeCopyGuestWithAttributes(NULL, (__bridge CFDictionaryRef)attributes, kSecCSDefaultFlags, &guest) == errSecSuccess
            && SecCodeCheckValidity(guest, kSecCSStrictValidate, NULL) == errSecSuccess
            && SecCodeCopyStaticCode(guest, kSecCSDefaultFlags, &actual) == errSecSuccess
            && SecStaticCodeCreateWithPath((__bridge CFURLRef)url, kSecCSDefaultFlags, &expected) == errSecSuccess
            && SecStaticCodeCheckValidity(expected, kSecCSStrictValidate, NULL) == errSecSuccess
            && SecCodeCopySigningInformation(actual, kSecCSSigningInformation, &actualInfo) == errSecSuccess
            && SecCodeCopySigningInformation(expected, kSecCSSigningInformation, &expectedInfo) == errSecSuccess;
        if (valid) {
            CFTypeRef a = CFDictionaryGetValue(actualInfo, kSecCodeInfoUnique), e = CFDictionaryGetValue(expectedInfo, kSecCodeInfoUnique);
            valid = a != NULL && e != NULL && CFEqual(a, e);
        }
        if (actualInfo) CFRelease(actualInfo); if (expectedInfo) CFRelease(expectedInfo);
        if (guest) CFRelease(guest); if (actual) CFRelease(actual); if (expected) CFRelease(expected);
        return valid ? EGRESS_MAC_OK : EGRESS_MAC_UNAVAILABLE;
    }
}
int32_t egress_mac_set_owner_mode(const char *path, uint32_t uid, uint32_t gid, uint32_t mode)
{
    if (!root_allowed() || !egress_mac_path_is_absolute(path) || (mode & ~0777u) != 0) return EGRESS_MAC_UNAVAILABLE;
    struct stat value;
    if (lstat(path, &value) != 0 || S_ISLNK(value.st_mode)) return EGRESS_MAC_INVALID;
    return chown(path, uid, gid) == 0 && chmod(path, mode) == 0 ? EGRESS_MAC_OK : EGRESS_MAC_IO;
}
int32_t egress_mac_bind_socket(int32_t descriptor, const char *name)
{
    if (denied() || name == NULL) return EGRESS_MAC_UNAVAILABLE;
    unsigned int index = if_nametoindex(name); if (index == 0) return EGRESS_MAC_UNAVAILABLE;
    struct sockaddr_storage local = {0}; socklen_t length = sizeof(local);
    if (getsockname(descriptor, (struct sockaddr *)&local, &length) != 0) return EGRESS_MAC_IO;
    int status = -1;
    if (local.ss_family == AF_INET) status = setsockopt(descriptor, IPPROTO_IP, IP_BOUND_IF, &index, sizeof(index));
    if (local.ss_family == AF_INET6) status = setsockopt(descriptor, IPPROTO_IPV6, IPV6_BOUND_IF, &index, sizeof(index));
    return status == 0 ? EGRESS_MAC_OK : EGRESS_MAC_IO;
}
int32_t egress_mac_write_dns(const uint8_t *json, size_t length, const char *server_ip, int32_t restore)
{
    @autoreleasepool { @try {
        if (!root_allowed() || json == NULL || length == 0 || length > 2 * 1024 * 1024) return EGRESS_MAC_UNAVAILABLE;
        unsigned char address[16];
        if (!restore && (server_ip == NULL || (inet_pton(AF_INET, server_ip, address) != 1 && inet_pton(AF_INET6, server_ip, address) != 1))) return EGRESS_MAC_INVALID;
        NSArray *snapshots = [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:json length:length] options:0 error:nil];
        if (![snapshots isKindOfClass:[NSArray class]] || snapshots.count == 0 || snapshots.count > 128) return EGRESS_MAC_INVALID;
        SCPreferencesRef preferences = SCPreferencesCreate(NULL, CFSTR("EgressController.Helper"), NULL);
        if (preferences == NULL) return EGRESS_MAC_UNAVAILABLE;
        BOOL ok = SCPreferencesLock(preferences, true);
        for (NSDictionary *snapshot in snapshots) {
            if (!ok || ![snapshot isKindOfClass:[NSDictionary class]]) { ok = NO; break; }
            NSString *serviceId = snapshot[@"serviceId"];
            if (![serviceId isKindOfClass:[NSString class]] || [[NSUUID alloc] initWithUUIDString:serviceId] == nil) { ok = NO; break; }
            SCNetworkServiceRef service = SCNetworkServiceCopy(preferences, (__bridge CFStringRef)serviceId);
            if (service == NULL) { ok = NO; break; }
            SCNetworkProtocolRef protocol = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeDNS);
            BOOL existed = [snapshot[@"protocolWasPresent"] boolValue];
            if (restore && !existed) {
                if (protocol != NULL) ok = SCNetworkServiceRemoveProtocolType(service, kSCNetworkProtocolTypeDNS);
            } else {
                if (protocol == NULL && SCNetworkServiceAddProtocolType(service, kSCNetworkProtocolTypeDNS)) protocol = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeDNS);
                if (protocol == NULL) ok = NO;
                else if (!restore) ok = SCNetworkProtocolSetConfiguration(protocol, (__bridge CFDictionaryRef)@{ @"ServerAddresses":@[[NSString stringWithUTF8String:server_ip]] }) && SCNetworkProtocolSetEnabled(protocol, true);
                else {
                    NSString *encoded = snapshot[@"originalConfigurationPlistBase64"];
                    id configuration = nil;
                    if ([encoded isKindOfClass:[NSString class]]) {
                        NSData *data = [[NSData alloc] initWithBase64EncodedString:encoded options:0];
                        configuration = data == nil ? nil : [NSPropertyListSerialization propertyListWithData:data options:NSPropertyListImmutable format:NULL error:nil];
                        if (![configuration isKindOfClass:[NSDictionary class]]) ok = NO;
                    }
                    if (ok) ok = SCNetworkProtocolSetConfiguration(protocol, (__bridge CFDictionaryRef)configuration)
                        && SCNetworkProtocolSetEnabled(protocol, [snapshot[@"protocolWasEnabled"] boolValue]);
                }
            }
            if (protocol) CFRelease(protocol); CFRelease(service);
        }
        if (ok) ok = SCPreferencesCommitChanges(preferences) && SCPreferencesApplyChanges(preferences);
        SCPreferencesUnlock(preferences); CFRelease(preferences);
        // Reopen the committed store; a successful setter is not proof that the exact saved
        // protocol presence, enabled state and dictionary have been restored.
        if (ok && restore) {
            SCPreferencesRef verify = SCPreferencesCreate(NULL, CFSTR("EgressController.VerifyRecovery"), NULL);
            if (verify == NULL) ok = NO;
            else {
                for (NSDictionary *snapshot in snapshots) {
                    SCNetworkServiceRef service = SCNetworkServiceCopy(verify, (__bridge CFStringRef)snapshot[@"serviceId"]);
                    if (service == NULL) { ok = NO; break; }
                    SCNetworkProtocolRef protocol = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeDNS);
                    BOOL existed = [snapshot[@"protocolWasPresent"] boolValue];
                    if ((protocol != NULL) != existed) ok = NO;
                    if (protocol != NULL && existed) {
                        id expected = nil; NSString *encoded = snapshot[@"originalConfigurationPlistBase64"];
                        if ([encoded isKindOfClass:[NSString class]]) {
                            NSData *data = [[NSData alloc] initWithBase64EncodedString:encoded options:0];
                            expected = [NSPropertyListSerialization propertyListWithData:data options:NSPropertyListImmutable format:NULL error:nil];
                        }
                        id actual = (__bridge id)SCNetworkProtocolGetConfiguration(protocol);
                        ok = ok && SCNetworkProtocolGetEnabled(protocol) == [snapshot[@"protocolWasEnabled"] boolValue]
                            && ((actual == nil && expected == nil) || [actual isEqual:expected]);
                    }
                    if (protocol != NULL) CFRelease(protocol); CFRelease(service);
                    if (!ok) break;
                }
                CFRelease(verify);
            }
        }
        return ok ? EGRESS_MAC_OK : EGRESS_MAC_IO;
    } @catch (NSException *exception) { (void)exception; return EGRESS_MAC_INTERNAL; } }
}
int32_t egress_mac_tun_present(const char *name)
{
    if (denied() || name == NULL || strncmp(name, "utun", 4) != 0) return EGRESS_MAC_UNAVAILABLE;
    return if_nametoindex(name) != 0 ? 1 : 0;
}
static int address_bytes(const struct sockaddr *value, int family, unsigned char *bytes, size_t count)
{
    memset(bytes, 0, count); if (value == NULL) return 0;
    size_t offset = family == AF_INET ? 4 : 8;
    if (value->sa_len <= offset) return 1;
    size_t available = value->sa_len - offset; if (available > count) available = count;
    memcpy(bytes, (const unsigned char *)value + offset, available); return 1;
}
int32_t egress_mac_tun_takeover(const char *name, const char *ipv4_cidr)
{
    if (denied() || name == NULL || strncmp(name, "utun", 4) != 0 || ipv4_cidr == NULL) return EGRESS_MAC_UNAVAILABLE;
    unsigned int index = if_nametoindex(name); if (index == 0) return 0;
    char ip[INET_ADDRSTRLEN] = {0}; size_t ipLength = strcspn(ipv4_cidr, "/");
    if (ipLength >= sizeof(ip)) return EGRESS_MAC_INVALID;
    memcpy(ip, ipv4_cidr, ipLength); struct in_addr expected;
    if (inet_pton(AF_INET, ip, &expected) != 1) return EGRESS_MAC_INVALID;
    uint32_t gateway = ntohl(expected.s_addr);
    if (gateway == UINT32_MAX) return EGRESS_MAC_INVALID;
    expected.s_addr = htonl(gateway + 1); // Pinned Xray's nextDarwinLocalIPv4; the configured gateway is the peer/DNS address.
    struct ifaddrs *interfaces = NULL; if (getifaddrs(&interfaces) != 0) return EGRESS_MAC_IO;
    BOOL ownsAddress = NO;
    for (struct ifaddrs *item = interfaces; item != NULL; item = item->ifa_next)
        if (strcmp(item->ifa_name, name) == 0 && (item->ifa_flags & IFF_UP) && item->ifa_addr != NULL && item->ifa_addr->sa_family == AF_INET
            && ((struct sockaddr_in *)item->ifa_addr)->sin_addr.s_addr == expected.s_addr) ownsAddress = YES;
    freeifaddrs(interfaces); if (!ownsAddress) return 0;
    int mib[] = {CTL_NET, PF_ROUTE, 0, AF_UNSPEC, NET_RT_DUMP2, 0}; size_t length = 0;
    if (sysctl(mib, 6, NULL, &length, NULL, 0) != 0 || length == 0 || length > 16 * 1024 * 1024) return EGRESS_MAC_IO;
    unsigned char *routes = malloc(length); if (routes == NULL) return EGRESS_MAC_INTERNAL;
    if (sysctl(mib, 6, routes, &length, NULL, 0) != 0) { free(routes); return EGRESS_MAC_IO; }
    int32_t result = egress_mac_route_dump_covers_tun(routes, length, index);
    free(routes); return result;
}
int32_t egress_mac_route_dump_covers_tun(const uint8_t *routes, size_t length, uint32_t index)
{
    if (routes == NULL || length == 0 || length > 16 * 1024 * 1024 || index == 0) return EGRESS_MAC_INVALID;
    BOOL v4default = NO, v6default = NO, v4low = NO, v4high = NO, v6low = NO, v6high = NO;
    uint32_t v4protected = 0, v6protected = 0;
    BOOL malformed = NO; size_t position = 0;
    for (; position + sizeof(struct rt_msghdr2) <= length;) {
        struct rt_msghdr2 copied; memcpy(&copied, routes + position, sizeof(copied)); const struct rt_msghdr2 *route = &copied;
        if (route->rtm_version != RTM_VERSION || route->rtm_msglen < sizeof(*route) || route->rtm_msglen > length - position) { malformed = YES; break; }
        if (route->rtm_index == index && (route->rtm_flags & RTF_UP) && !(route->rtm_flags & (RTF_REJECT | RTF_BLACKHOLE | RTF_HOST | RTF_IFSCOPE))) {
            struct sockaddr *values[RTAX_MAX] = {0}; size_t cursor = position + sizeof(*route), end = position + route->rtm_msglen;
            BOOL parsed = YES;
            for (int slot = 0; slot < RTAX_MAX; slot++) if (route->rtm_addrs & (1 << slot)) {
                if (cursor + 2 > end) { parsed = NO; break; }
                struct sockaddr *value = (struct sockaddr *)(routes + cursor);
                size_t step = value->sa_len == 0 ? 4 : ((size_t)value->sa_len + 3) & ~(size_t)3;
                if (step > end - cursor) { parsed = NO; break; }
                values[slot] = value; cursor += step;
            }
            if (!parsed) { malformed = YES; break; }
            if (values[RTAX_DST] != NULL) {
                int family = values[RTAX_DST]->sa_family; unsigned char dst[16], mask[16]; size_t count = family == AF_INET ? 4 : 16;
                if ((family == AF_INET && values[RTAX_DST]->sa_len < 8) || (family == AF_INET6 && values[RTAX_DST]->sa_len < 24)) { malformed = YES; break; }
                if ((family == AF_INET || family == AF_INET6) && address_bytes(values[RTAX_DST], family, dst, count)) {
                    address_bytes(values[RTAX_NETMASK], family, mask, count);
                    BOOL tailZero = YES; for (size_t byte = 1; byte < count; byte++) if (dst[byte] || mask[byte]) tailZero = NO;
                    if (tailZero && dst[0] == 0 && mask[0] == 0) { if (family == AF_INET) v4default = YES; else v6default = YES; }
                    if (tailZero && mask[0] == 0x80 && dst[0] == 0) { if (family == AF_INET) v4low = YES; else v6low = YES; }
                    if (tailZero && mask[0] == 0x80 && dst[0] == 0x80) { if (family == AF_INET) v4high = YES; else v6high = YES; }
                    for (unsigned int bit = 0; bit < 8; bit++)
                        if (tailZero && dst[0] == (1u << bit) && mask[0] == (uint8_t)(0xffu << bit)) {
                            if (family == AF_INET) v4protected |= 1u << bit; else v6protected |= 1u << bit;
                        }
                }
            }
        }
        position += route->rtm_msglen;
    }
    if (position != length) malformed = YES;
    if (malformed) return EGRESS_MAC_IO;
    return (v4protected == 255 || v4default || (v4low && v4high))
        && (v6protected == 255 || v6default || (v6low && v6high)) ? 1 : 0;
}
