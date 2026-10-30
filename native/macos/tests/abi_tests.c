// SPDX-License-Identifier: MIT
// Only pure ABI probes. This executable never inspects applications/interfaces/DNS/icons.
#include "egress_mac.h"
#include <assert.h>
#include <stdlib.h>
#include <string.h>
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
    return 0;
}
