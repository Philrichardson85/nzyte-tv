# Checkpoint 3B3-B1 Raspberry Pi deployment plan

This document is a **candidate, operator-reviewed plan** for a later Raspberry Pi acceptance checkpoint. Nothing in this repository applies the plan automatically. Do not run it against production without an approved maintenance window, current backups, and an operator present to verify both local station state and public YouTube playback.

Checkpoint 3B3-B1 keeps the application media root at `/srv/nzyte-tv/media`. It moves the complete mutable programming catalog from the exFAT media filesystem to ext4 at `/var/lib/nzyte-tv-programming/catalog`, then bind-mounts that directory at the existing application path `/srv/nzyte-tv/media/catalog`. `ProgrammingPaths` therefore continues to resolve the normalized library and programming files without an application-specific catalog override:

```text
/srv/nzyte-tv/media/library
/srv/nzyte-tv/media/catalog/song-catalog.json
/srv/nzyte-tv/media/catalog/programming.json
```

The source directory on ext4 is an implementation detail of deployment. The broadcaster and operations helper continue to use the bind-mounted application path.

## Why the catalog moves to ext4

The production media volume is exFAT and is mounted with volume-wide `uid`, `gid`, and `fmask` options. It cannot provide the per-directory POSIX ownership, set-group-ID inheritance, and narrow group write boundary needed by `nzyte-ops`. The ext4 root filesystem can provide those controls.

The whole current catalog directory must be migrated as one unit. The verified inventory currently includes active `song-catalog.json` and `programming.json` plus `programming.pre-short-run.json`, `song-catalog.before-new-aliases.json`, and `song-catalog.empty-init.json`. Re-inventory the directory at deployment time rather than treating that list as permanently exhaustive. Files left physically underneath the bind mount on exFAT are rollback evidence after successful migration. Do not delete them as part of this checkpoint.

## Candidate systemd assets

The repository provides these inactive templates:

- `deploy/checkpoint-3b3-b1/nzyte-tv-programming-catalog.mount.template` binds the ext4 catalog onto the established application path.
- `deploy/checkpoint-3b3-b1/nzyte-tv.service.d/programming-catalog.conf` is a candidate production-service drop-in that requires the catalog mount.
- `deploy/checkpoint-3b3-b1/nzyte-tv-operations.service` requires the same mount and starts only the Unix-socket helper.
- `deploy/checkpoint-3b3-b1/nzyte-tv-dashboard.service` starts the loopback dashboard under its dedicated identity.

A systemd mount unit must be named for its mount target. Because a Windows checkout cannot safely carry the literal backslash in the escaped unit name, install the neutral template on Linux using the exact name returned by:

```bash
systemd-escape --path --suffix=mount /srv/nzyte-tv/media/catalog
```

The expected output and Linux destination are:

```text
srv-nzyte\x2dtv-media-catalog.mount
/etc/systemd/system/srv-nzyte\x2dtv-media-catalog.mount
```

Do not substitute another unit name. The mount template requires and follows the parent `srv-nzyte\x2dtv-media.mount`. Both the broadcaster drop-in and operations helper require the catalog mount, so a missing bind mount prevents startup instead of revealing the stale exFAT catalog underneath.

## Service identities and permissions

Create these identities only during the reviewed Pi deployment:

- `nzyte-programming`: contains `u24` and `nzyte-ops`; it must not contain `nzyte-dashboard`.
- `nzyte-tv-state`: contains `u24` and `nzyte-dashboard`; it should not contain `nzyte-ops` without a demonstrated future requirement.
- `nzyte-ops`: is also the operations-helper primary group and socket-access group; `nzyte-dashboard` joins it only to connect to the Unix socket.

The intended programming storage is:

```text
/var/lib/nzyte-tv-programming/catalog  u24:nzyte-programming  2770
catalog JSON files                     u24:nzyte-programming  0660
```

Keeping `u24` in `nzyte-programming` preserves existing CLI/operator programming workflows. The directory set-group-ID bit causes adjacent mutation-lock, temporary, and replacement files to inherit `nzyte-programming`. The helper's `UMask=0007` turns ordinary `0666` file creation into `0660`, so atomic replacement remains readable and writable by both `u24` and `nzyte-ops`. A replacement inode created by the helper will normally be owned by `nzyte-ops:nzyte-programming`, while one later created by a CLI operation will normally be `u24:nzyte-programming`; the shared group and `0660` mode, rather than a permanently fixed file owner, preserve mutual access. Pi acceptance must verify actual ownership and mode after a controlled mutation.

The intended runtime-state storage is:

```text
/var/lib/nzyte-tv                         u24:nzyte-tv-state  2750
state.json                                u24:nzyte-tv-state  0640
rolling-state.json                        u24:nzyte-tv-state  0640
rolling-state.json.replenishment.json     u24:nzyte-tv-state  0640
```

The production broadcaster remains `User=u24`, `Group=u24`, `UMask=0027` and receives `nzyte-tv-state` as a supplementary group. Its existing base unit declares `StateDirectory=nzyte-tv`, which otherwise lets systemd assign the state directory to the service's primary `u24` group. The candidate B1 drop-in clears that `StateDirectory` declaration after the migration pre-provisions the directory; it does not delete the existing directory. This is necessary to preserve the accepted `u24:nzyte-tv-state` set-group-ID ownership. The set-group-ID state directory gives newly created files the `nzyte-tv-state` group; normal `0666` creation under that umask yields `0640`. NZYTE TV's state writer creates its temporary file in the destination directory and atomically renames the same inode over the destination, so that inherited group and mode survive replacement. Pi acceptance must verify this behavior on the target system after state advances.

The dashboard remains `User=nzyte-dashboard`, `Group=nzyte-dashboard`, with `SupplementaryGroups=nzyte-tv-state nzyte-ops`. It is not a member of `nzyte-programming` and receives no direct `/srv/nzyte-tv` access. The operations service is `User=nzyte-ops`, `Group=nzyte-ops`, with `SupplementaryGroups=nzyte-programming`. Its systemd write allowlist remains limited to `/srv/nzyte-tv/media/catalog` and `/run/nzyte-tv-operations`; the library, source, playlists, media-root parent, `/etc/nzyte-tv`, and `/var/lib/nzyte-tv` are not writable by the helper. Both service sandboxes hide the physical `/var/lib/nzyte-tv-programming` source; only systemd mounts it, and the helper uses the established bind-mounted application path.

The operations runtime directory is owned by `nzyte-ops`, uses mode `0750`, and the helper uses `UMask=0007`. The dashboard's membership in `nzyte-ops` is intended to permit Unix-socket connection without granting catalog access. Verify the created socket owner, group, and mode during Pi acceptance before enabling browser mutations.

## Staged migration and acceptance procedure

The commands and exact package-transfer mechanism must be reviewed against the Pi at acceptance time. Do not copy secrets, print environment files, change the RTMP destination, or overwrite a live application directory.

1. Verify current production health, rolling advancement, and moving video **and audible audio** on public YouTube.
2. Record secret-free service, mount, state, release, and file-hash evidence sufficient to identify the accepted starting point. Do not collect environment contents, process command lines, FFmpeg stderr, or journal excerpts containing sensitive data.
3. Create the dedicated `nzyte-dashboard` and `nzyte-ops` service users and the `nzyte-programming` and `nzyte-tv-state` groups. Apply only the memberships described above.
4. Create `/var/lib/nzyte-tv-programming/catalog` on ext4 with the intended owner, group, and mode.
5. Copy the **entire** current `/srv/nzyte-tv/media/catalog` into a separately named ext4 staging/rollback location, then populate the intended ext4 catalog. Include active and backup JSON files; retain the exFAT originals.
6. Place the catalog mount template under its exact `systemd-escape`-derived Linux unit name, but do not activate it yet.
7. Stage the candidate broadcaster drop-in so the production service will fail closed on the catalog mount. Preserve its existing requirements for `/var/lib/nzyte-tv` and `/srv/nzyte-tv/media`. Confirm that the manually provisioned state directory is correct before the drop-in clears systemd's `StateDirectory` ownership management.
8. Stage new dashboard and operations binaries in new versioned/candidate directories and stage their candidate service units. Do not publish over an accepted live directory.
9. Begin the final consistency window by stopping the production broadcaster cleanly.
10. Verify that the broadcaster and its FFmpeg child have exited before changing the catalog mount.
11. Perform a final catalog synchronization to ext4 and compare a complete, deterministic file list and cryptographic hashes between the exFAT source and ext4 destination.
12. Apply `u24:nzyte-programming`, directory mode `2770`, and file mode `0660` to the ext4 catalog. Apply the accepted `nzyte-tv-state` group and modes to the state directory and dashboard-consumed state files.
13. Reload systemd configuration, activate the catalog bind mount, and verify that its source, target, filesystem relationship, and dependency on the parent media mount are exactly as expected.
14. Through `/srv/nzyte-tv/media/catalog`, compare the application-visible file list and hashes with the verified ext4 migration source. Confirm `song-catalog.json`, `programming.json`, and every backup JSON are present.
15. Start and verify the operations helper. Confirm it has only a Unix socket, the socket's access is limited as intended, and the helper can read current Spotlight state without a mutation.
16. Start the production broadcaster using the accepted production start path.
17. Verify the catalog bind mount, production service, one expected broadcaster, one expected FFmpeg process, advancing rolling state, unchanged committed blocks, dashboard status, and public YouTube moving video **and audible audio**.
18. Only after local and public broadcast acceptance, cut over the dashboard service identity and candidate dashboard version. Preserve the previously accepted dashboard as a rollback candidate.
19. Verify Spotlight GET and catalog options without mutating programming configuration.
20. Perform a controlled Spotlight mutation only with explicit operator approval. Use a known expected revision and an approved existing catalog song.
21. Verify exactly one revision increment, correct atomic file ownership/mode, preserved unrelated programming fields, unchanged active/committed blocks, uninterrupted broadcaster/FFmpeg processes, advancing state, and public YouTube video and audio.

Do not consider local process health alone sufficient public-stream acceptance.

## Rollback procedure

Choose rollback actions from observed evidence; do not assume which migration stage failed.

1. Preserve secret-free evidence of the failure and the last known-good stage.
2. Stop the candidate dashboard and operations helper as needed to prevent new mutations.
3. Stop the broadcaster cleanly and verify its FFmpeg child has exited before changing the catalog mount.
4. Unmount and disable the catalog bind mount. If necessary, remove or disable the candidate broadcaster drop-in so the accepted prior service can use the underlying exFAT catalog.
5. Verify that the revealed exFAT catalog is the retained rollback copy before restarting anything. Do not delete or overwrite the ext4 evidence during incident analysis.
6. Restore the accepted prior dashboard unit/binary and identities if dashboard cutover had occurred.
7. Restart the broadcaster through its accepted production procedure.
8. Verify local state, rolling progression, the expected single broadcaster and FFmpeg process, dashboard status as applicable, and public YouTube moving video **and audible audio**.

If the underlying exFAT catalog cannot be positively identified as the accepted prior catalog, stop and restore from a separately verified rollback copy instead of guessing.

## Deferred 3B3-B2 storage decision

This ext4 bind-mount design solves only Spotlight configuration and catalog permissions. A later media-refresh operation would need to create or update metadata sidecars under the exFAT source/library trees. This checkpoint does not grant that access and does not establish a safe B2 permission model. Checkpoint 3B3-B2 requires a separate, explicit storage and permission design before implementation.
