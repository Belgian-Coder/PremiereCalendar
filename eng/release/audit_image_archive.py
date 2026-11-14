"""Bind the exported image archive to the build metadata, the source archive proof and the local image.

Inputs are files only (no Docker call): the bundle's build-metadata.json and source-archive-verification.json, the
docker-save archive named in the metadata, and the JSON array printed by `docker image inspect <tag>`. The report
<bundle>/release-<version>-audit.json is created exclusively; the exit code is non-zero when any check fails.
"""
import argparse
import hashlib
import json
import pathlib
import tarfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--image-tag', required=True)
    parser.add_argument('--image-inspect', required=True, help='file holding `docker image inspect <tag>` JSON')
    args = parser.parse_args()
    release = pathlib.Path(args.bundle)
    meta = json.loads((release / 'build-metadata.json').read_text(encoding='utf-8-sig'))
    proof_path = release / 'source-archive-verification.json'
    proof = json.loads(proof_path.read_text(encoding='utf-8-sig'))
    source_path = release / f'source-{args.version}.tar'
    output = release / f'release-{args.version}-audit.json'
    if output.exists():
        raise SystemExit(f'Refusing to overwrite existing audit evidence: {output}')
    with source_path.open('rb') as stream:
        source_sha = hashlib.file_digest(stream, 'sha256').hexdigest()
    source_proof_pass = (proof.get('allChecksPass') is True and not proof.get('mismatches')
                         and proof.get('version') == args.version and proof.get('sourceRevision') == meta['sourceRevision']
                         and proof.get('sourceArchiveSha256') == meta.get('sourceArchiveSha256') == source_sha
                         and proof.get('verifiedFiles', {}).get('.cs', 0) > 0
                         and hashlib.sha256(proof_path.read_bytes()).hexdigest() == meta.get('sourceArchiveVerificationSha256'))
    assert meta['version'] == args.version
    assert len(meta.get('sourceRevision', '')) == 40
    archive_path = release / meta['archive']
    with tarfile.open(archive_path) as archive:
        manifest = json.load(archive.extractfile('manifest.json'))[0]
        raw = archive.extractfile(manifest['Config']).read()
        config = json.loads(raw)
    image = json.loads(pathlib.Path(args.image_inspect).read_text(encoding='utf-8-sig'))[0]
    archive_sha = hashlib.sha256(archive_path.read_bytes()).hexdigest()
    labels = config.get('config', {}).get('Labels', {})
    report = {
        'version': meta['version'], 'sourceRevision': meta['sourceRevision'], 'buildId': meta['buildId'],
        'archive': meta['archive'], 'archiveSha256': archive_sha, 'expectedArchiveSha256': meta['archiveSha256'],
        'archiveBytes': archive_path.stat().st_size, 'expectedArchiveBytes': meta['bytes'],
        'archiveTag': manifest['RepoTags'][0],
        'archiveConfigurationDigest': 'sha256:' + hashlib.sha256(raw).hexdigest(),
        'archiveOs': config.get('os'), 'archiveArchitecture': config.get('architecture'), 'archiveLabels': labels,
        'archiveLayers': [x.split('/')[-1] for x in manifest['Layers']],
        'localImageId': image['Id'], 'localOs': image['Os'], 'localArchitecture': image['Architecture'],
        'localLabels': image['Config'].get('Labels', {}), 'localRootFsLayers': image['RootFS']['Layers'],
        'checks': {
            'sourceArchiveCommittedBlobParity': source_proof_pass,
            'archiveHashMatchesMetadata': archive_sha == meta['archiveSha256'],
            'archiveBytesMatchMetadata': archive_path.stat().st_size == meta['bytes'],
            'archiveTagIsExpected': manifest['RepoTags'] == [args.image_tag],
            'localImageIdMatchesMetadata': image['Id'] == meta['localImageIdentity'],
            'archiveLabelsMatchMetadata': labels == meta['labels'],
            'localLabelsMatchMetadata': image['Config'].get('Labels', {}) == meta['labels'],
            'sourceRevisionMatches': labels.get('org.opencontainers.image.revision') == meta['sourceRevision'],
            'versionLabelMatches': labels.get('org.opencontainers.image.version') == meta['version'],
            'linuxAmd64Matches': config.get('os') == image['Os'] == meta['os'] and config.get('architecture') == image['Architecture'] == meta['architecture'],
            'archiveLocalLayerCountsMatch': len(manifest['Layers']) == len(image['RootFS']['Layers'])},
        'layerIdentityNote': 'docker-save archive layer digests and local RootFS diff IDs are distinct representations; layer count is compared.'}
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'configurationDigest': report['archiveConfigurationDigest'], 'checks': report['checks']}, indent=2))
    raise SystemExit(0 if all(report['checks'].values()) else 1)


if __name__ == '__main__':
    main()
