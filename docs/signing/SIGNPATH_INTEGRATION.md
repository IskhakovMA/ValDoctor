# SignPath ↔ GitHub Actions integration plan (draft, do not enable before acceptance)

Source: [SignPath — GitHub trusted build system](https://docs.signpath.io/trusted-build-systems/github).

## Principles

- No private key ever exists in GitHub or in this repository: keys stay in SignPath; GitHub only holds an API token.
- Only artifacts built by this repository's workflow on GitHub-hosted runners can be signed (SignPath verifies the
  origin with GitHub, not with data from the build script).
- Every signing request is approved manually by an approver in SignPath.
- Unsigned or unapproved builds are never published as a release.

## Setup (after acceptance, by the maintainer)

1. In SignPath: link GitHub.com to the organization and to the ValDoctor project; create the signing policy
   (e.g. `release-signing`) with manual approval; artifact configuration that signs `ValDoctor.exe` and enforces
   ProductName `ValDoctor` and ProductVersion equal to the tag.
2. Create a SignPath CI user with *submitter* rights only; store its token as the repository secret
   `SIGNPATH_API_TOKEN` (this is the only secret).
3. Restrict the policy to the `release` workflow and to tags `v*` (SignPath origin restrictions).

## Workflow change (future PR, sketch)

In `release.yml`, between the existing `build` job and `publish`:

```yaml
  sign:
    needs: [version, build]
    runs-on: ubuntu-latest
    permissions:
      actions: read      # SignPath reads the workflow artifact
      contents: read
    steps:
      - id: sign
        uses: signpath/github-action-submit-signing-request@v3   # pin to a commit SHA
        with:
          api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
          organization-id: <SignPath organization id>
          project-slug: valdoctor
          signing-policy-slug: release-signing
          github-artifact-id: ${{ needs.build.outputs.artifact-id }}   # build.yml must expose the upload-artifact id
          wait-for-completion: true
          output-artifact-directory: signed
      - run: (cd signed && sha256sum ValDoctor.exe > ValDoctor.exe.sha256)
      - uses: actions/upload-artifact@<sha>
        with: { name: valdoctor-win-x64-signed, path: signed/ }
```

`publish` then downloads `valdoctor-win-x64-signed` instead of the unsigned artifact. Additional checks to add:
verify the Authenticode signature on a Windows runner (`Get-AuthenticodeSignature` → `Valid`, signer
"SignPath Foundation") and re-run the smoke test on the signed EXE before publishing.

## Open items

- `build.yml` must output the `upload-artifact` step's `artifact-id`.
- Decide whether the SHA-256 published with the release is computed from the signed file (yes — the signed file is
  what users download).
- Keep the unsigned build only as an internal CI artifact.
