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
 * The remaining exports read platform state; there are NO privileged mutation exports. */
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
#ifdef __cplusplus
}
#endif
#endif
