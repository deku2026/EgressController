// SPDX-License-Identifier: MIT
// Independent implementation. ArcForges was reviewed for ABI architecture only; no source was copied.
#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <SystemConfiguration/SystemConfiguration.h>
#include "egress_mac.h"
#include <stdlib.h>
#include <string.h>

static int32_t copy_bytes(const void *bytes, size_t length, uint8_t *output, size_t capacity, size_t *required)
{
    if (required == NULL) return EGRESS_MAC_INVALID;
    *required = 0;
    if (output == NULL && capacity != 0) return EGRESS_MAC_INVALID;
    *required = length;
    if (capacity < length || output == NULL) return EGRESS_MAC_BUFFER_SMALL;
    if (length != 0) memcpy(output, bytes, length);
    return EGRESS_MAC_OK;
}
static int32_t fail(size_t *required, int32_t status) { if (required != NULL) *required = 0; return status; }
static int32_t copy_json(id object, uint8_t *output, size_t capacity, size_t *required)
{
    NSError *error = nil;
    NSData *data = [NSJSONSerialization dataWithJSONObject:object options:NSJSONWritingSortedKeys error:&error];
    if (data == nil) return fail(required, EGRESS_MAC_INTERNAL);
    return copy_bytes(data.bytes, data.length, output, capacity, required);
}
static NSString *absolute_path(const char *path)
{
    if (!egress_mac_path_is_absolute(path)) return nil;
    NSString *text = [[NSString alloc] initWithUTF8String:path];
    return text.isAbsolutePath ? text : nil;
}
static NSString *string_or_empty(CFStringRef value) { return value == NULL ? @"" : (__bridge NSString *)value; }

uint32_t egress_mac_abi_version(void) { return 1; }
int32_t egress_mac_path_is_absolute(const char *path) { return path != NULL && path[0] == '/'; }
int32_t egress_mac_copy_abi_info_json(uint8_t *output, size_t capacity, size_t *required)
{
    static const char info[] = "{\"abiVersion\":1,\"privilegedMutationAvailable\":false}";
    return copy_bytes(info, sizeof(info) - 1, output, capacity, required);
}
int32_t egress_mac_copy_canonical_path(const char *path, uint8_t *output, size_t capacity, size_t *required)
{
    if (!egress_mac_path_is_absolute(path)) return fail(required, EGRESS_MAC_INVALID);
    char *canonical = realpath(path, NULL);
    if (canonical == NULL) return fail(required, EGRESS_MAC_IO);
    int32_t status = copy_bytes(canonical, strlen(canonical), output, capacity, required);
    free(canonical);
    return status;
}
int32_t egress_mac_copy_bundle_json(const char *path, uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { @try {
        NSString *bundlePath = absolute_path(path);
        if (bundlePath == nil || ![bundlePath.pathExtension.lowercaseString isEqualToString:@"app"]) return fail(required, EGRESS_MAC_INVALID);
        NSBundle *bundle = [NSBundle bundleWithPath:bundlePath];
        if (bundle == nil || bundle.bundleIdentifier.length == 0) return fail(required, EGRESS_MAC_UNAVAILABLE);
        NSString *canonical = bundle.bundleURL.URLByResolvingSymlinksInPath.path;
        NSString *executable = bundle.executableURL.URLByResolvingSymlinksInPath.path;
        NSString *name = [bundle objectForInfoDictionaryKey:@"CFBundleDisplayName"];
        if (name == nil) name = [bundle objectForInfoDictionaryKey:@"CFBundleName"];
        if (name == nil) name = bundlePath.lastPathComponent.stringByDeletingPathExtension;
        return copy_json(@{ @"bundleIdentifier":bundle.bundleIdentifier, @"displayName":name,
            @"bundlePath":canonical, @"primaryExecutablePath":executable != nil ? executable : (id)[NSNull null] }, output, capacity, required);
    } @catch (NSException *exception) { (void)exception; return fail(required, EGRESS_MAC_INTERNAL); } }
}
int32_t egress_mac_copy_icon_png(const char *path, uint32_t pixels, uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { @try {
        NSString *bundlePath = absolute_path(path);
        if (bundlePath == nil || pixels < 16 || pixels > 512 || ![bundlePath.pathExtension.lowercaseString isEqualToString:@"app"])
            return fail(required, EGRESS_MAC_INVALID);
        if (![[NSFileManager defaultManager] fileExistsAtPath:bundlePath]) return fail(required, EGRESS_MAC_IO);
        NSImage *icon = [[NSWorkspace sharedWorkspace] iconForFile:bundlePath];
        NSRect bounds = NSMakeRect(0, 0, pixels, pixels);
        CGImageRef image = [icon CGImageForProposedRect:&bounds context:nil hints:nil];
        if (image == NULL) return fail(required, EGRESS_MAC_UNAVAILABLE);
        NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithCGImage:image];
        NSData *png = [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        if (png == nil || png.length > 16 * 1024 * 1024) return fail(required, EGRESS_MAC_IO);
        return copy_bytes(png.bytes, png.length, output, capacity, required);
    } @catch (NSException *exception) { (void)exception; return fail(required, EGRESS_MAC_INTERNAL); } }
}
int32_t egress_mac_copy_interfaces_json(uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { @try {
        SCPreferencesRef preferences = SCPreferencesCreate(NULL, CFSTR("EgressController.ReadOnly"), NULL);
        if (preferences == NULL) return fail(required, EGRESS_MAC_UNAVAILABLE);
        CFArrayRef services = SCNetworkServiceCopyAll(preferences);
        CFRelease(preferences);
        if (services == NULL) return fail(required, EGRESS_MAC_UNAVAILABLE);
        SCDynamicStoreRef store = SCDynamicStoreCreate(NULL, CFSTR("EgressController.ReadOnly"), NULL, NULL);
        NSMutableArray *result = [NSMutableArray array];
        for (CFIndex index = 0; index < CFArrayGetCount(services); index++) {
            SCNetworkServiceRef service = (SCNetworkServiceRef)CFArrayGetValueAtIndex(services, index);
            SCNetworkInterfaceRef interface = SCNetworkServiceGetInterface(service);
            if (interface == NULL) continue;
            NSString *bsd = string_or_empty(SCNetworkInterfaceGetBSDName(interface));
            if (bsd.length == 0) continue;
            NSMutableArray *addresses = [NSMutableArray array];
            BOOL active = NO;
            if (store != NULL) {
                NSString *linkKey = [NSString stringWithFormat:@"State:/Network/Interface/%@/Link", bsd];
                NSDictionary *link = CFBridgingRelease(SCDynamicStoreCopyValue(store, (__bridge CFStringRef)linkKey));
                active = [link[@"Active"] boolValue];
                for (NSString *protocol in @[@"IPv4", @"IPv6"]) {
                    NSString *key = [NSString stringWithFormat:@"State:/Network/Interface/%@/%@", bsd, protocol];
                    NSDictionary *value = CFBridgingRelease(SCDynamicStoreCopyValue(store, (__bridge CFStringRef)key));
                    if ([value[@"Addresses"] isKindOfClass:[NSArray class]]) [addresses addObjectsFromArray:value[@"Addresses"]];
                }
            }
            [result addObject:@{ @"serviceId":string_or_empty(SCNetworkServiceGetServiceID(service)), @"bsdName":bsd,
                @"displayName":string_or_empty(SCNetworkServiceGetName(service)), @"interfaceType":string_or_empty(SCNetworkInterfaceGetInterfaceType(interface)),
                @"hardwareAddress":string_or_empty(SCNetworkInterfaceGetHardwareAddressString(interface)), @"isActive":@(active), @"addresses":addresses }];
        }
        if (store != NULL) CFRelease(store);
        CFRelease(services);
        return copy_json(result, output, capacity, required);
    } @catch (NSException *exception) { (void)exception; return fail(required, EGRESS_MAC_INTERNAL); } }
}
int32_t egress_mac_copy_dns_json(uint8_t *output, size_t capacity, size_t *required)
{
    @autoreleasepool { @try {
        SCPreferencesRef preferences = SCPreferencesCreate(NULL, CFSTR("EgressController.ReadOnly"), NULL);
        if (preferences == NULL) return fail(required, EGRESS_MAC_UNAVAILABLE);
        CFArrayRef services = SCNetworkServiceCopyAll(preferences);
        CFRelease(preferences);
        if (services == NULL) return fail(required, EGRESS_MAC_UNAVAILABLE);
        NSMutableArray *result = [NSMutableArray array];
        for (CFIndex index = 0; index < CFArrayGetCount(services); index++) {
            SCNetworkServiceRef service = (SCNetworkServiceRef)CFArrayGetValueAtIndex(services, index);
            SCNetworkProtocolRef protocol = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeDNS);
            id configuration = protocol == NULL ? nil : (__bridge id)SCNetworkProtocolGetConfiguration(protocol);
            NSData *plist = configuration == nil ? nil : [NSPropertyListSerialization dataWithPropertyList:configuration format:NSPropertyListXMLFormat_v1_0 options:0 error:nil];
            if (configuration != nil && plist == nil) {
                if (protocol != NULL) CFRelease(protocol);
                CFRelease(services);
                return fail(required, EGRESS_MAC_IO);
            }
            [result addObject:@{ @"serviceId":string_or_empty(SCNetworkServiceGetServiceID(service)),
                @"protocolWasPresent":@(protocol != NULL), @"protocolWasEnabled":@(protocol != NULL && SCNetworkProtocolGetEnabled(protocol)),
                @"originalConfigurationPlistBase64":plist == nil ? (id)[NSNull null] : [plist base64EncodedStringWithOptions:0] }];
            if (protocol != NULL) CFRelease(protocol);
        }
        CFRelease(services);
        return copy_json(result, output, capacity, required);
    } @catch (NSException *exception) { (void)exception; return fail(required, EGRESS_MAC_INTERNAL); } }
}
