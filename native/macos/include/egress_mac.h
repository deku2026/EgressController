// SPDX-License-Identifier: MIT
#ifndef EGRESS_MAC_H
#define EGRESS_MAC_H
#include <stddef.h>
#include <stdint.h>
#define EGRESS_MAC_API __attribute__((visibility("default")))
#ifdef __cplusplus
extern "C" {
#endif
/* ABI 1: cdecl, fixed-width scalar statuses, UTF-8 inputs, no native-owned pointers.
 * All output bytes are caller-owned. required is mandatory and is reset on errors.
 * NULL/0 is a size query. BUFFER_SMALL never writes output. JSON/text excludes NUL.
 * No exception crosses this boundary. Only the ABI/path probes are pure functions.
 * Remaining exports read platform state or explicitly guard root-only mutations and mock denial. */
enum { EGRESS_MAC_OK = 0, EGRESS_MAC_BUFFER_SMALL = 1, EGRESS_MAC_INVALID = -1,
       EGRESS_MAC_UNAVAILABLE = -2, EGRESS_MAC_IO = -3, EGRESS_MAC_INTERNAL = -4 };
EGRESS_MAC_API uint32_t egress_mac_abi_version(void);
EGRESS_MAC_API int32_t egress_mac_path_is_absolute(const char *path_utf8);
EGRESS_MAC_API int32_t egress_mac_copy_abi_info_json(uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_canonical_path(const char *path_utf8, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_bundle_json(const char *path_utf8, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_icon_png(const char *path_utf8, uint32_t pixels, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_interfaces_json(uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_dns_json(uint8_t *output, size_t capacity, size_t *required);
/* One gated live settings capture; binary plist bytes remain caller-owned and bounded to 256KiB. */
EGRESS_MAC_API int32_t egress_mac_copy_resource_proxy_settings_plist(uint8_t *output, size_t capacity, size_t *required);
/* Pure decision from supplied plist only. Never reads current settings, executes PAC or consults credential stores. */
EGRESS_MAC_API int32_t egress_mac_resolve_resource_proxy_json(const char *url_utf8, const uint8_t *settings_plist, size_t length,
    uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API uint32_t egress_mac_uid(void);
EGRESS_MAC_API uint32_t egress_mac_gid(void);
EGRESS_MAC_API uint32_t egress_mac_euid(void);
EGRESS_MAC_API int32_t egress_mac_copy_peer_json(int32_t descriptor, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_process_json(int32_t pid, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_file_info(const char *path, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_copy_fd_info(int32_t descriptor, uint8_t *output, size_t capacity, size_t *required);
EGRESS_MAC_API int32_t egress_mac_verify_process_code(int32_t pid, const char *expected_path);
EGRESS_MAC_API int32_t egress_mac_set_owner_mode(const char *path, uint32_t uid, uint32_t gid, uint32_t mode);
EGRESS_MAC_API int32_t egress_mac_bind_socket(int32_t descriptor, const char *interface_name);
EGRESS_MAC_API int32_t egress_mac_write_dns(const uint8_t *snapshots_json, size_t length, const char *server_ip, int32_t restore);
EGRESS_MAC_API int32_t egress_mac_tun_present(const char *name);
EGRESS_MAC_API int32_t egress_mac_tun_takeover(const char *name, const char *ipv4_cidr);
/* Pure route-byte classifier. Never queries interfaces or the routing table. */
EGRESS_MAC_API int32_t egress_mac_route_dump_covers_tun(const uint8_t *data, size_t length, uint32_t interface_index);
#ifdef __cplusplus
}
#endif
#endif
