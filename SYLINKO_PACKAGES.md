# Sylinko package publishing

`release-sylinko-nuget.yml` publishes Porta.Pty and Porta.Pty.Native under their
original IDs.

The release workflow generates a stable package version as
`999.YYYYMMDD.<github.run_number>`, for example `999.20261001.42`. The date
comes from the original workflow run's creation time in UTC. All packages and
platforms from that run share the same version, including when jobs are rerun.
Run numbers belong to this workflow; keep one publishing workflow per package.
Trigger it through workflow_dispatch (no version input), or push a `publish-*`
source tag. The generated version is written to the workflow summary, and the
publishing action creates a release tagged `nuget/<producer>/<version>`.

The workflow refuses to upload over a release that already contains assets,
because the feed action otherwise uses `--clobber`. If publishing stopped after
uploading assets, retain that release and start a new run to publish a new
version. Check any existing feed PR before completing or abandoning it.
The local draft version `999.0.0` is only for local builds and smoke tests; never
publish it. Package release versions do not change assembly or file versions.

Configure `SYLINKO_NUGET_FEED_TOKEN` as in Sylinko/Microsoft.ML and register both
original IDs in Sylinko/nuget-feed before the first run.

The workflow builds all four Unix native libraries, then runs
`scripts/pack-sylinko.ps1`. That script builds three AnyCPU managed implementations,
checks their public API consistency and creates packages with a shared reference
assembly, OS-specific runtime assemblies and architecture-specific native assets.
For local packing, run the native build script on Linux and macOS and combine its
four output files under `src/Porta.Pty.Native/output/runtimes` first.

Porta.Pty depends on Porta.Pty.Native and Microsoft.Windows.Console.ConPTY.
Its transitive build target distributes ConPTY's OpenConsole hosts, which are
not otherwise transitive build assets of the ConPTY package. These hosts must
stay synchronized with the pinned ConPTY dependency version.

Package smoke tests run an actual shell process in the published output for each
of the five supported application RIDs. Windows also checks RID-less consumption
to cover the other package output layout. Unix jobs only publish and run once;
they do not repeat the RID-less build. Tests use a private package cache and
restore the exact newly packed version from the local package directory.
The publishing action creates a GitHub Release and a feed PR; merge that PR to
make the new versions available. Never replace a published ID/version in place.
