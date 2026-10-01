<#
.SYNOPSIS
    Inicia a distro WSL do D.A.N.T.E. no logon do Windows e a mantém ativa.

.DESCRIPTION
    Registra a tarefa agendada "DANTE WSL" para o usuário atual. No logon, ela abre a distro sem janela
    (wslg.exe) com um processo "sleep infinity", que impede o WSL de desligar a distro por ociosidade.
    Com a distro ativa, o systemd inicia o serviço do usuário dante.service (deploy/dante-service.sh install).
    Roda sem privilégios de administrador e não guarda senha nem segredo.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\windows\Register-DanteAutostart.ps1 -Distro Ubuntu

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\windows\Register-DanteAutostart.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [string] $Distro,
    [switch] $Unregister
)

$ErrorActionPreference = 'Stop'
$taskName = 'DANTE WSL'

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        "Tarefa '$taskName' removida. A distro deixa de iniciar no logon; o serviço systemd continua instalado."
    }
    else { "Tarefa '$taskName' não encontrada." }
    return
}

if (-not $Distro) { throw 'Informe a distro com -Distro <nome>. Liste as instaladas com: wsl -l -v' }

$env:WSL_UTF8 = '1'
$installed = @(wsl.exe --list --quiet | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($installed -notcontains $Distro) {
    throw "Distro WSL '$Distro' não encontrada. Instaladas: $($installed -join ', ')"
}

# wslg.exe is the windowless WSL launcher; wsl.exe would keep a console window open for the whole session.
$launcher = Join-Path $env:ProgramFiles 'WSL\wslg.exe'
if (-not (Test-Path $launcher)) { throw "wslg.exe não encontrado em $launcher. Atualize o WSL com: wsl --update" }

$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $launcher -Argument "-d `"$Distro`" -- /bin/sleep infinity"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
# If the keep-alive ends abnormally (e.g. after "wsl --shutdown"), Task Scheduler starts it again.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName -Description 'Mantém a distro WSL do D.A.N.T.E. ativa desde o logon.' `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

"Tarefa '$taskName' registrada para $user (distro $Distro)."
"Para iniciar agora sem reiniciar: Start-ScheduledTask -TaskName '$taskName'"
