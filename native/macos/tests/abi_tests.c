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
int main(void)
{
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
    pure_route_tests();
    return 0;
}
