// SPDX-License-Identifier: MIT
// Synthetic sysctl/path callbacks only. No live processes, sockets or privileges.
#include "../src/process_identity.h"
#include <assert.h>

static int calls;
static int scenario;
static int fake_query(int *mib, unsigned int count, void *output, size_t *length, void *input, size_t input_length)
{
    assert(count == 4 && mib[0] == CTL_KERN && mib[1] == KERN_PROC && mib[2] == KERN_PROC_PID && mib[3] == 42);
    assert(input == NULL && input_length == 0 && *length == sizeof(struct kinfo_proc));
    calls++;
    if (scenario == 1) { errno = EPERM; return -1; }
    struct kinfo_proc *info = output;
    info->kp_proc.p_pid = scenario == 2 ? 43 : 42;
    info->kp_proc.p_starttime.tv_sec = scenario == 3 ? 0 : 123;
    info->kp_proc.p_starttime.tv_usec = 456;
    info->kp_eproc.e_ucred.cr_uid = 0; // root peer observed by the ordinary GUI
    info->kp_eproc.e_ucred.cr_ngroups = 1;
    info->kp_eproc.e_ucred.cr_groups[0] = 0;
    if (scenario == 4) *length = 0; // process exited
    if (scenario == 5) (*length)--; // truncated metadata
    if (calls == 2 && scenario == 6) info->kp_proc.p_starttime.tv_usec++;
    if (calls == 2 && scenario == 7) info->kp_eproc.e_ucred.cr_uid = 501;
    if (calls == 2 && scenario == 8) info->kp_eproc.e_ucred.cr_groups[0] = 20;
    if (scenario == 11) info->kp_eproc.e_ucred.cr_ngroups = 0;
    return 0;
}
static int fake_path(int pid, void *output, uint32_t length)
{
    assert(pid == 42 && length == PROC_PIDPATHINFO_MAXSIZE);
    if (scenario == 9) { errno = ESRCH; return 0; }
    const char *path = scenario == 10 ? "relative/helper" : "/synthetic/root-helper";
    strcpy(output, path); return (int)strlen(path);
}
int main(void)
{
    struct egress_process_identity identity;
    assert(egress_read_process_identity(42, fake_query, fake_path, &identity) == 0);
    assert(calls == 2 && identity.uid == 0 && identity.gid == 0 && identity.pid == 42);
    assert(identity.started_at == UINT64_C(123000456) && strcmp(identity.path, "/synthetic/root-helper") == 0);
    for (scenario = 1; scenario <= 11; scenario++) {
        calls = 0; errno = 0;
        assert(egress_read_process_identity(42, fake_query, fake_path, &identity) == -1);
        assert(errno != 0);
        if (scenario == 1) assert(errno == EPERM);
        if (scenario >= 6 && scenario <= 8) assert(errno == EAGAIN);
    }
    calls = 0;
    assert(egress_read_process_identity(0, fake_query, fake_path, &identity) == -1 && calls == 0 && errno == EINVAL);
    return 0;
}
