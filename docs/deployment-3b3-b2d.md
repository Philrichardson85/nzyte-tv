# Checkpoint 3B3-B2-D candidate activation plan

This is a workstation-prepared candidate design. It has not been installed or accepted on the Raspberry Pi. Do not treat the presence of these files as production activation.

## Storage and identities

Production media remains read-only to the Operations helper at `/srv/nzyte-tv/media`. External programming metadata uses the separately provisioned ext4 directory `/var/lib/nzyte-tv-media-metadata`, owned for the `nzyte-media-metadata` group. `u24` and `nzyte-ops` are members; `nzyte-dashboard` is not.

The B2-D Operations candidate keeps the accepted B1 catalog and Unix-socket permissions, adds `nzyte-media-metadata`, and enables the B2-C feature using trusted service configuration. Its only new persistent writable root is `/var/lib/nzyte-tv-media-metadata`. The omitted inbox setting deliberately resolves to `/srv/nzyte-tv/media/inbox`; that tree remains read-only to the helper.

The broadcaster candidate drop-in retains the accepted catalog-mount and state-directory requirements, adds group-read access to the metadata root, and declares that root read-only in its service filesystem namespace. The dashboard receives neither group membership nor filesystem access to external metadata.

## Gated activation order

1. Verify the accepted station, rolling buffer, dashboard, Spotlight, storage mounts, and public moving video with audible audio before changing anything.
2. Stage and inspect candidate binaries and units without replacing the accepted deployment.
3. Verify `/var/lib/nzyte-tv-media-metadata` is ext4, empty for first bootstrap, set-group-ID, and accessible only to the intended identities.
4. Run the explicit adjacent-sidecar bootstrap preview, review every result, then publish generation/revision 1 only with separate operator approval.
5. Verify `current.json`, generation 1, inventory, ownership, modes, and hashes. Do not delete or rewrite any adjacent sidecar.
6. Add `assetMetadataStorage.mode=externalGeneration` and `assetMetadataStorage.externalRoot=/var/lib/nzyte-tv-media-metadata` to the staged rolling configuration. Validate the staged configuration before it can start a broadcaster.
7. Install the candidate Operations unit and broadcaster drop-in through the operator-reviewed deployment procedure. Do not alter RTMP configuration or secrets.
8. Use the normal graceful broadcaster cutover procedure once, because the running process must be started with the new rolling configuration. This one-time activation restart is distinct from later metadata refreshes.
9. Verify the pre-cutover active and committed blocks, planner lineage, durable intents, rolling buffer, single broadcaster/FFmpeg ownership, dashboard, and public moving video with audible audio.
10. Verify a newly captured planning snapshot records external generation 1. Do not regenerate or rewrite existing blocks to force this result.
11. Enable and verify Media Operations only after read-only status and Spotlight remain healthy. A later refresh publication must not require a broadcaster restart.

## Compatibility and rollback

Adjacent `.nzytetv.meta.json` files remain required rollback and recovery evidence. Frozen snapshots with null metadata generation and revision permanently use adjacent authority, including pre-cutover active blocks, committed blocks, and durable intents. Never remove those sidecars during B2-D.

If acceptance fails, determine whether metadata bootstrap, rolling configuration, the Operations candidate, or broadcaster composition caused the failure before changing state. Stop the candidate processes cleanly, restore the accepted rolling configuration and service/drop-in set, and restart through the accepted handoff procedure. Leave the external metadata store and every media/adjacent file intact for diagnosis. Verify local rolling state and public moving video with audible audio after rollback.

## Deferred decisions for Pi acceptance

- Confirm effective systemd sandbox paths and supplementary groups with the candidate identities.
- Confirm newly created metadata files inherit `nzyte-media-metadata` and remain readable by `u24`.
- Confirm the read-only inbox can be enumerated through the media mount without granting exFAT writes.
- Confirm bootstrap and external generation validation complete within an acceptable maintenance window.
- Capture the exact staged configuration, binary hashes, unit diffs, and rollback evidence before activation.
