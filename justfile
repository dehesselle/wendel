# SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
#
# SPDX-License-Identifier: 0BSD

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

box := "clouding-windows"
remote := "C:/src/wendel"

# SSH into a devbox defined in .devbox/ssh_config, e.g. `just ssh hetzner-linux`
ssh host *args:
    ssh -F .devbox/ssh_config {{host}} {{args}}

# Copy the sources to the devbox, replacing what is there
push:
    ssh -F .devbox/ssh_config {{box}} 'Remove-Item -Recurse -Force -ErrorAction SilentlyContinue {{remote}}; New-Item -ItemType Directory -Force {{remote}} | Out-Null'
    scp -F .devbox/ssh_config -r Wendel.slnx Directory.Build.props src tests tools {{box}}:{{remote}}/

# Build and test on the devbox
build:
    ssh -F .devbox/ssh_config {{box}} 'cd {{remote}}; dotnet build -c Release -warnaserror; if ($LASTEXITCODE) { exit $LASTEXITCODE }; dotnet test -c Release --no-build; exit $LASTEXITCODE'

# Copy the built exe from the devbox into the project root
pull:
    scp -F .devbox/ssh_config {{box}}:{{remote}}/src/Wendel/bin/Release/net48/wendel.exe wendel.exe

# Push, build, test and pull in one go
all: push build pull
