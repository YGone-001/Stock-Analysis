# Branch protection requirements

This repository's branch-protection settings are administered in GitHub repository settings and are not represented by source-controlled workflow files. They must be verified or applied by a repository administrator.

| Branch | Required status checks |
| --- | --- |
| `main` | `build-test-windows`, `market-data-server-test`, `reject-large-blobs` |
| `develop` | `build-test-windows`, `market-data-server-test` |

Do not rename these CI job names without first updating the corresponding GitHub branch-protection rules. The release workflow is tag-triggered and is not a required pull-request check.

Current source-tree status: **ADMIN ACTION REQUIRED**. The local development environment has no authenticated GitHub CLI session, and unauthenticated branch-protection API requests are not sufficient to verify effective rules.
