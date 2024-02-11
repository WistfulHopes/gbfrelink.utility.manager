# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/gbfrelink.utility.manager/*" -Force -Recurse
dotnet publish "./gbfrelink.utility.manager.csproj" -c Release -o "$env:RELOADEDIIMODS/gbfrelink.utility.manager" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location