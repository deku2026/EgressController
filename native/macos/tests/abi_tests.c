// SPDX-License-Identifier: MIT
// Only pure ABI probes. This executable never inspects applications/interfaces/DNS/icons.
#include "egress_mac.h"
#include <assert.h>
#include <stdlib.h>
#include <string.h>
#include <net/route.h>
#include <netinet/in.h>

static size_t fake_route(uint8_t *bytes, int family, int flags, uint16_t index)
{
    const size_t address_size = family == AF_INET ? sizeof(struct sockaddr_in) : sizeof(struct sockaddr_in6);
    const size_t length = sizeof(struct rt_msghdr2) + address_size;
    memset(bytes, 0, length);
    struct rt_msghdr2 header = {0}; header.rtm_msglen = (uint16_t)length; header.rtm_version = RTM_VERSION;
    header.rtm_flags = flags; header.rtm_index = index; header.rtm_addrs = RTA_DST;
    memcpy(bytes, &header, sizeof(header));
    bytes[sizeof(header)] = (uint8_t)address_size; bytes[sizeof(header) + 1] = (uint8_t)family;
    return length;
}
static void pure_route_tests(void)
{
    uint8_t routes[8192];
    size_t first = fake_route(routes, AF_INET, RTF_UP, 42);
    size_t second = fake_route(routes + first, AF_INET6, RTF_UP, 42);
    assert(egress_mac_route_dump_covers_tun(routes, first + second, 42) == 1);
    assert(egress_mac_route_dump_covers_tun(routes, first + second, 43) == 0);
    assert(egress_mac_route_dump_covers_tun(routes, first, 42) == 0);
    first = fake_route(routes, AF_INET, RTF_UP | RTF_IFSCOPE, 42);
    assert(egress_mac_route_dump_covers_tun(routes, first + second, 42) == 0);
    first = fake_route(routes, AF_INET, RTF_UP | RTF_HOST, 42);
    assert(egress_mac_route_dump_covers_tun(routes, first + second, 42) == 0);
    first = fake_route(routes, AF_INET, RTF_UP, 42);
    routes[first + second] = 0;
    assert(egress_mac_route_dump_covers_tun(routes, first + second + 1, 42) == EGRESS_MAC_IO);
    routes[sizeof(struct rt_msghdr2)] = 2; // Truncated destination cannot masquerade as a default route.
    assert(egress_mac_route_dump_covers_tun(routes, first + second, 42) == EGRESS_MAC_IO);
    size_t total = 0;
    for (int family_index = 0; family_index < 2; family_index++) for (unsigned int bit = 0; bit < 8; bit++) {
        int family = family_index == 0 ? AF_INET : AF_INET6;
        size_t address_size = family == AF_INET ? sizeof(struct sockaddr_in) : sizeof(struct sockaddr_in6);
        size_t route_size = fake_route(routes + total, family, RTF_UP | RTF_STATIC, 42);
        struct rt_msghdr2 header; memcpy(&header, routes + total, sizeof(header));
        header.rtm_msglen = (uint16_t)(route_size + address_size); header.rtm_addrs |= RTA_NETMASK;
        memcpy(routes + total, &header, sizeof(header));
        size_t address_offset = family == AF_INET ? 4 : 8;
        routes[total + sizeof(header) + address_offset] = (uint8_t)(1u << bit);
        memset(routes + total + route_size, 0, address_size);
        routes[total + route_size] = (uint8_t)address_size; routes[total + route_size + 1] = (uint8_t)family;
        routes[total + route_size + address_offset] = (uint8_t)(0xffu << bit);
        total += header.rtm_msglen;
    }
    assert(egress_mac_route_dump_covers_tun(routes, total, 42) == 1); // Exact eight protected prefixes per family used by pinned Xray.
    struct rt_msghdr2 first_header; memcpy(&first_header, routes, sizeof(first_header));
    first_header.rtm_flags |= RTF_IFSCOPE; memcpy(routes, &first_header, sizeof(first_header));
    assert(egress_mac_route_dump_covers_tun(routes, total, 42) == 0); // One scoped prefix is not global coverage.
}
static void pure_proxy_snapshot_tests(void)
{
    /* All settings are synthetic. This calls only the supplied-plist resolver, never system settings capture. */
    static const uint8_t settings[] = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>"
        "<key>HTTPSEnable</key><integer>1</integer><key>HTTPSProxy</key><string>127.0.0.1</string>"
        "<key>HTTPSPort</key><integer>7890</integer><key>ExceptionsList</key><array><string>bypass.example.test</string></array>"
        "</dict></plist>";
    uint8_t output[4096] = {0}; size_t required = 0;
    assert(egress_mac_resolve_resource_proxy_json("https://resource.example.test/source", settings, sizeof(settings) - 1,
        NULL, 0, &required) == EGRESS_MAC_BUFFER_SMALL);
    assert(required > 0 && required < sizeof(output));
    assert(egress_mac_resolve_resource_proxy_json("https://resource.example.test/source", settings, sizeof(settings) - 1,
        output, sizeof(output) - 1, &required) == EGRESS_MAC_OK);
    assert(strstr((const char *)output, "127.0.0.1") != NULL);
    assert(strstr((const char *)output, "7890") != NULL);
    memset(output, 0, sizeof(output));
    assert(egress_mac_resolve_resource_proxy_json("https://bypass.example.test/source", settings, sizeof(settings) - 1,
        output, sizeof(output) - 1, &required) == EGRESS_MAC_OK);
    assert(strstr((const char *)output, "\"type\":\"direct\"") != NULL);
    assert(egress_mac_resolve_resource_proxy_json("https://resource.example.test/source", (const uint8_t *)"invalid", 7,
        output, sizeof(output), &required) == EGRESS_MAC_INVALID);
    static const uint8_t pac[] = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>"
        "<key>ProxyAutoConfigEnable</key><integer>1</integer>"
        "<key>ProxyAutoConfigURLString</key><string>https://pac.example.invalid/private?token=not-forwarded</string>"
        "</dict></plist>";
    memset(output, 0, sizeof(output));
    assert(egress_mac_resolve_resource_proxy_json("https://resource.example.test/source", pac, sizeof(pac) - 1,
        output, sizeof(output) - 1, &required) == EGRESS_MAC_OK);
    assert(strstr((const char *)output, "\"type\":\"pac\"") != NULL);
    assert(strstr((const char *)output, "not-forwarded") == NULL);
}
int main(void)
{
    for (int active = 0; active <= 1; active++) for (int address = 0; address <= 1; address++) {
        uint8_t result[2048] = {0}; size_t size = 0;
        assert(egress_mac_interface_fixture(active, address, result, sizeof(result) - 1, &size) == EGRESS_MAC_OK);
        assert(strstr((const char *)result, active || address ? "\"isActive\":true" : "\"isActive\":false") != NULL);
    }
    assert(egress_mac_abi_version() == 1);
    assert(egress_mac_path_is_absolute("/synthetic/Example.app") == 1);
    assert(egress_mac_path_is_absolute("Applications/Example.app") == 0);
    assert(egress_mac_path_is_absolute(NULL) == 0);
    assert(egress_mac_copy_abi_info_json(NULL, 0, NULL) == EGRESS_MAC_INVALID);
    size_t required = 0;
    assert(egress_mac_copy_abi_info_json(NULL, 0, &required) == EGRESS_MAC_BUFFER_SMALL);
    assert(required > 0 && required < 256);
    uint8_t *bytes = malloc(required + 1);
    assert(bytes != NULL);
    memset(bytes, 0x5a, required + 1);
    const size_t length = required;
    assert(egress_mac_copy_abi_info_json(bytes, required - 1, &required) == EGRESS_MAC_BUFFER_SMALL);
    for (size_t index = 0; index <= length; index++) assert(bytes[index] == 0x5a);
    assert(egress_mac_copy_abi_info_json(bytes, length, &required) == EGRESS_MAC_OK);
    assert(required == length && bytes[length] == 0x5a);
    assert(memcmp(bytes, "{\"abiVersion\":1,", 16) == 0);
    free(bytes);
    /* Run under EGRESS_MOCK_ONLY too: no file, user, socket or TUN access. */
    uint8_t metadata[1024] = {0};
    assert(egress_mac_file_info_fixture(0100600, metadata, sizeof(metadata) - 1, &required) == EGRESS_MAC_OK);
    assert(strstr((const char *)metadata, "\"IsRegular\":true") != NULL);
    assert(strstr((const char *)metadata, "\"IsDirectory\":false") != NULL);
    assert(strstr((const char *)metadata, "\"IsSocket\":false") != NULL);
    assert(strstr((const char *)metadata, "\"IsSymbolicLink\":false") != NULL);
    pure_route_tests();
    pure_proxy_snapshot_tests();
    return 0;
}
