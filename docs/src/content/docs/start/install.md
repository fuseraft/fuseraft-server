---
title: Install
description: Download a release archive or build fuseraft-web from source, and install the fuseraft CLI it runs.
---

fuseraft-web is a .NET 10 server that runs the `fuseraft` CLI. You need both.

## The fuseraft CLI

Install [fuseraft](https://github.com/fuseraft/fuseraft-cli/releases) v1.13.0 or later, which speaks
wire protocol version 1. Put it on `PATH`, or point [`Fuseraft:Binary`](/fuseraft-web/reference/config/)
at it.

Daemons you [attach to](/fuseraft-web/guides/daemons/) need v1.13.0 or later too, for the attach
WebSocket, and v1.14.0 or later for `wss://`, for picking tasks up again after a reconnect, and for
keeping them across a daemon restart.

## A release archive

Download the archive for your platform from
[Releases](https://github.com/fuseraft/fuseraft-web/releases): linux-x64, linux-arm64, osx-x64,
osx-arm64 or win-x64. The archives are self-contained, so the machine doesn't need .NET.

```bash
mkdir fuseraft-web && tar -xzf fuseraft-web-<version>-linux-x64.tar.gz -C fuseraft-web
cd fuseraft-web && ./fuseraft-web
```

On Windows, unzip the archive and run `fuseraft-web.exe`.

`appsettings.json` sits next to the binary. See [Configuration](/fuseraft-web/reference/config/) for
what goes in it.

## Linux keychain

On Linux, install `secret-tool` (from `libsecret-tools`) so API keys go in the keychain. Without it,
or without a desktop session to unlock the keyring, they fall back to mode-600 files in each
account's fuseraft home.

## From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/fuseraft/fuseraft-web.git
cd fuseraft-web
./build.sh                      # build + test
./build.sh publish              # framework-dependent build in artifacts/publish/portable/
./build.sh publish linux-x64    # self-contained single binary in artifacts/publish/linux-x64/
./build.sh pack linux-x64       # …and archived as artifacts/fuseraft-web-<version>-linux-x64.tar.gz
```

On Windows, `.\build.ps1` takes the same targets. The version comes from the latest `v*` git tag.

To run from source, use `dotnet run`. It picks up `ASPNETCORE_ENVIRONMENT=Development` from
`Properties/launchSettings.json`.
