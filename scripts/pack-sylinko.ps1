param([string]$Version = '999.0.0')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    # Use representative RIDs to set compile-time OS switches. The managed
    # implementations are AnyCPU; native assets are selected separately by RID.
    foreach ($platform in @('win', 'linux', 'osx')) {
        $rid = "$platform-x64"
        dotnet build src/Porta.Pty/Porta.Pty.csproj -c Release -r $rid "-p:PackageVersion=$Version"
        if ($LASTEXITCODE) { throw "Managed build failed for $rid." }
        $output = "artifacts/managed/$platform"
        New-Item -ItemType Directory -Force "$output/ref" | Out-Null
        Copy-Item "src/Porta.Pty/bin/Release/net11.0/$rid/Porta.Pty.dll" $output
        Copy-Item "src/Porta.Pty/obj/Release/net11.0/$rid/ref/Porta.Pty.dll" "$output/ref"
    }

    dotnet run --project tools/VerifyManagedApi/VerifyManagedApi.csproj -- artifacts/managed/win/Porta.Pty.dll artifacts/managed/linux/Porta.Pty.dll artifacts/managed/osx/Porta.Pty.dll
    if ($LASTEXITCODE) { throw 'Platform implementations must expose the same public API.' }

    dotnet pack src/Porta.Pty.Native/Porta.Pty.Native.csproj -c Release "-p:PackageVersion=$Version"
    if ($LASTEXITCODE) { throw 'Native package creation failed. Build all four native assets first.' }

    # Re-evaluate dependencies without a RID; the package carries all platforms.
    dotnet restore src/Porta.Pty/Porta.Pty.csproj "-p:PackageVersion=$Version"
    if ($LASTEXITCODE) { throw 'Package restore failed.' }
    dotnet pack src/Porta.Pty/Porta.Pty.csproj -c Release --no-build "-p:PackageVersion=$Version"
    if ($LASTEXITCODE) { throw 'Managed package creation failed.' }
}
finally {
    Pop-Location
}
