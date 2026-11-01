// SPDX-License-Identifier: MIT
#ifndef EGRESS_PROCESS_IDENTITY_H
#define EGRESS_PROCESS_IDENTITY_H
#include <errno.h>
#include <libproc.h>
#include <stdint.h>
#include <string.h>
#include <sys/sysctl.h>

typedef int (*egress_process_query)(int *, unsigned int, void *, size_t *, void *, size_t);
typedef int (*egress_process_path_query)(int, void *, uint32_t);

struct egress_process_identity {
    uid_t uid;
    gid_t gid;
    pid_t pid;
    uint64_t started_at;
    char path[PROC_PIDPATHINFO_MAXSIZE];
};

static int egress_process_metadata(pid_t pid, egress_process_query query, struct egress_process_identity *identity)
{
    int mib[] = {CTL_KERN, KERN_PROC, KERN_PROC_PID, pid};
    struct kinfo_proc info = {0}; size_t length = sizeof(info);
    if (query(mib, 4, &info, &length, NULL, 0) != 0) return -1;
    if (length != sizeof(info) || info.kp_proc.p_pid != pid || info.kp_eproc.e_ucred.cr_ngroups < 1
        || info.kp_proc.p_starttime.tv_sec <= 0 || info.kp_proc.p_starttime.tv_usec < 0
        || info.kp_proc.p_starttime.tv_usec >= 1000000) { errno = ESRCH; return -1; }
    identity->uid = info.kp_eproc.e_ucred.cr_uid;
    identity->gid = info.kp_eproc.e_ucred.cr_groups[0];
    identity->pid = pid;
    identity->started_at = (uint64_t)info.kp_proc.p_starttime.tv_sec * UINT64_C(1000000) + info.kp_proc.p_starttime.tv_usec;
    return 0;
}

// Unprivileged PROC_PIDTBSDINFO callers need same-user access. KERN_PROC_PID
// exposes the required identity to the unprivileged GUI, including a root peer.
// Socket credentials and code signatures are still checked by the callers.
// The callbacks let regression tests exercise the real lookup without live PIDs.
static int egress_read_process_identity(pid_t pid, egress_process_query query,
    egress_process_path_query query_path, struct egress_process_identity *identity)
{
    if (pid <= 0) { errno = EINVAL; return -1; }
    memset(identity, 0, sizeof(*identity));
    if (egress_process_metadata(pid, query, identity) != 0) return -1;
    int length = query_path(pid, identity->path, sizeof(identity->path));
    if (length <= 0) return -1;
    if (length >= (int)sizeof(identity->path) || identity->path[0] != '/'
        || memchr(identity->path, 0, sizeof(identity->path)) == NULL) { errno = EINVAL; return -1; }
    struct egress_process_identity after = {0};
    if (egress_process_metadata(pid, query, &after) != 0) return -1;
    if (identity->uid != after.uid || identity->gid != after.gid || identity->pid != after.pid
        || identity->started_at != after.started_at) { errno = EAGAIN; return -1; }
    return 0;
}
#endif
