#ps1_sysnative
<#
.SYNOPSIS
    ALS Windows DEA installation script
.DESCRIPTION
    This script installs Windows DEA and all its dependencies
.PARAMETER messageBus
    ALS Nats endpoint.
.PARAMETER domain
    Domain used in the ALS deployment.
.PARAMETER index
    Windows DEA index in the deployment. Default 0.
    Note that each Windows DEA must have a unique index.
.PARAMETER dropletDir
    Target droplet directory. This is where all the droplets will be deployed. Default is C:\Droplets
.PARAMETER localRoute
    Used to determine the network interface.
    The application takes the interface used when trying to reach the provided IP. Default is 8.8.8.8
.PARAMETER statusPort
    Port for publishing an http endpoint used for monitoring.
    If 0, the Windows DEA takes the first available port. Default is 0.
.PARAMETER multiTenant
    Determine if muliple application can be deployed on the current DEA. Default true.
.PARAMETER maxMemoryMB
    Maximum megabytes that all the droplets in the DEA can use.
.PARAMETER heartBeatIntervalMS
    Time interval in milliseconds in which the Windows DEA sends its vitals to the Cloud Controller.
    Default is 10000.
.PARAMETER advertiseIntervalMS
    Time interval in milliseconds in which the Windows DEA verifies the deployed droplets.
    Default is 5000.    
.PARAMETER uploadThrottleBitsPS
    Used for limiting the network upload rate for each deployed app.
    If 0, limitation is disabled. Default is 0.
.PARAMETER maxConcurrentStarts  
    Determine the maximum amount of droplets that can start simultaneously. Default is 3.
.PARAMETER directoryServerPort  
    Port used for directory server. Default is 34567.
.PARAMETER streamingTimeoutMS
    Http timeout in milliseconds used for file streaming. Default is 60000.
.PARAMETER stagingEnabled
    Determine if the current DEA accepts staging requests. Default is true.
.PARAMETER stagingTimeoutMS
    Time in milliseconds after witch the Windows DEA marks the staging as failed. Default is 1200000.
.PARAMETER stack
    Name of the stack that is going to be announced to the Cloud Controller. Default windows2012.
.PARAMETER installDir
    Target install directory. This is where all the Windows DEA binaries will be installed. Default is C:\WinDEA
    If git is not installed on the system, this is where it is going to be installed
.PARAMETER deaDownloadURL
    URL of the DEA msi.
#>

[CmdletBinding()]
param (

    $messageBus = '',
    $domain = '',
    $index = 0,
    $dropletDir = 'C:\Droplets',
    $localRoute =  '8.8.8.8',
    $statusPort = 0,
    $multiTenant = 'true',
    $maxMemoryMB = 4096,
    $heartBeatIntervalMS = 10000,
    $advertiseIntervalMS = 5000,
    $uploadThrottleBitsPS = 0,
    $maxConcurrentStarts = 3,
    $directoryServerPort = 34567,
    $streamingTimeoutMS = 60000,
    $stagingEnabled = 'true',
    $stagingTimeoutMS = 1200000,
    $stack = "win2012",
    $installDir = 'C:\WinDEA',
    $logyardInstallDir = 'C:\logyard',
    $logyardRedisURL = '',
    $defaultGitPath = "c:\Users\cloudbase-init\appdata\local\programs\git\bin\git.exe",
    $deaDownloadURL = "http://rpm.uhurucloud.net/wininstaller/inst/deainstaller-1.2.30.msi",
    $logyardInstallerURL = "http://rpm.uhurucloud.net/wininstaller/inst/logyard-installer.exe",
    $zmqDownloadURL = "http://miru.hk/archive/ZeroMQ-3.2.4~miru1.0-x64.exe",
    $gitDownloadURL = "https://github.com/msysgit/msysgit/releases/download/Git-1.9.4-preview20140815/Git-1.9.4-preview20140815.exe"
)


$neccessaryFeatures = "Web-Server","Web-WebServer","Web-Common-Http","Web-Default-Doc","Web-Dir-Browsing","Web-Http-Errors","Web-Static-Content","Web-Http-Redirect","Web-Health","Web-Http-Logging","Web-Custom-Logging","Web-Log-Libraries","Web-ODBC-Logging","Web-Request-Monitor","Web-Http-Tracing","Web-Performance","Web-Stat-Compression","Web-Dyn-Compression","Web-Security","Web-Filtering","Web-Basic-Auth","Web-CertProvider","Web-Client-Auth","Web-Digest-Auth","Web-Cert-Auth","Web-IP-Security","Web-Url-Auth","Web-Windows-Auth","Web-App-Dev","Web-Net-Ext","Web-Net-Ext45","Web-AppInit","Web-ASP","Web-Asp-Net","Web-Asp-Net45","Web-CGI","Web-ISAPI-Ext","Web-ISAPI-Filter","Web-Includes","Web-WebSockets","Web-Mgmt-Tools","Web-Mgmt-Console","Web-Mgmt-Compat","Web-Metabase","Web-Lgcy-Mgmt-Console","Web-Lgcy-Scripting","Web-WMI","Web-Scripting-Tools","Web-Mgmt-Service","WAS","WAS-Process-Model","WAS-NET-Environment","WAS-Config-APIs","NET-Framework-Features","NET-Framework-Core","NET-Framework-45-Features","NET-Framework-45-Core","NET-Framework-45-ASPNET","NET-WCF-Services45","NET-WCF-HTTP-Activation45","Web-WHC"

$location = $pwd.Path
$tempDir = [System.Guid]::NewGuid().ToString()


function VerifyParameters{
    if ([string]::IsNullOrWhiteSpace($messageBus))
    {
        throw [System.ArgumentException] 'The messageBus parameter is mandatory.'
        exit 1
    }
    
    if ([string]::IsNullOrWhiteSpace($domain))
    {
        throw [System.ArgumentException] 'The domain parameter is mandatory.'
        exit 1
    }

    if ([string]::IsNullOrWhiteSpace($logyardRedisURL))
    {
        throw [System.ArgumentException] 'The logyardRedisURL parameter is mandatory.'
        exit 1
    }
}

function CheckFeatureDependency(){
    $featureStatus = Install-WindowsFeature $neccessaryFeatures
}

function CheckGit()
{
   Write-Host "Checking git"

   #check if default git location exists
   If (Test-Path $defaultGitPath){
     Write-Host "git installed on system" -ForegroundColor Green
        return $defaultGitPath
    }
    
    Write-Host "git not installed, trying to install ..."
    Write-Host "Downloading git from ${gitDownloadURL} ..."

    Invoke-WebRequest $gitDownloadURL -OutFile "Git-Install.exe"
    
    Write-Host "Installing git"
    $gitInstallFile = Join-Path -Path $env:temp -ChildPath "$tempDir\Git-Install.exe"
    $gitInstallArgs = "/silent"
    
    [System.Diagnostics.Process]::Start($gitInstallFile, $gitInstallArgs).WaitForExit()

    Write-Host "Done!" -ForegroundColor Green

    return $defaultGitPath
}

function CheckZMQ()
{
    Write-Host 'Checking if zmq is available ...'

    # check if zmq libraries are available
    $zmqExists = (Start-Process -FilePath 'cmd.exe' -ArgumentList '/c where libzmq-v120-mt-3_2_4.dll' -Wait -Passthru -NoNewWindow).ExitCode

    if ($zmqExists -ne 0)
    {
        Write-Host 'ZeroMQ libraries not found, downloading and installing ...'
        Write-Host "Downloading ZeroMQ installer from ${zmqDownloadURL} ..."
        Invoke-WebRequest $zmqDownloadURL -OutFile 'ZMQ-Install.exe'
        
        Write-Host 'Installing ZeroMQ ...'
        $zmqInstallFile = (Join-Path $env:temp (Join-Path $tempDir 'ZMQ-Install.exe'))
        $zmqInstallArgs = '/S /D=c:\zmq'
        
        [System.Diagnostics.Process]::Start($zmqInstallFile, $zmqInstallArgs).WaitForExit()
        
        Write-Host 'Updating PATH environment variable ...'
        [Environment]::SetEnvironmentVariable('Path', "${env:Path};c:\zmq\bin", [System.EnvironmentVariableTarget]::Machine )
    }

    Write-Host 'ZeroMQ check complete.' -ForegroundColor Green
}

function InstallLogyard()
{
    Write-Host 'Installing Logyard ...'

    Write-Host "Downloading Logyard installer from ${logyardInstallerURL} ..."
    Invoke-WebRequest $logyardInstallerURL -OutFile 'Logyard-Install.exe'
    
    Write-Host 'Installing Logyard ...'
    $logyardInstallFile = (Join-Path $env:temp (Join-Path $tempDir 'Logyard-Install.exe'))
    $logyardInstallArgs = '/Q'
    
    $env:LOGYARD_REDIS = $logyardRedisURL

    Start-Process -FilePath $logyardInstallFile -ArgumentList $logyardInstallArgs -Wait -Passthru -NoNewWindow
    
    Get-Content 'c:\logyard-setup.log'
    
    Write-Host 'Logyard installation complete.' -ForegroundColor Green
}

function InstallDEA($gitLocation){
    Write-Host "Downloading Windows DEA"
    Invoke-WebRequest $deaDownloadURL -OutFile "DEAInstaller.msi"
    $deaInstallFile = Join-Path -Path $env:temp -ChildPath "$tempDir\DEAInstaller.msi"
    $deaArgs =  "/c", "msiexec", "/i", "`"$deaInstallFile`"", "/qn",  "INSTALLDIR=`"$installDir`""
    $deaArgs += "MessageBus=$messageBus", "Domain=$domain", "Index=$index", "Stacks=$stack"
    $deaArgs += "LocalRoute=$localRoute", "StatusPort=$statusPort", "MultiTenant=$multiTenant"
    $deaArgs += "MaxMemoryMB=$maxMemoryMB", "HeartBeatIntervalMS=$heartBeatIntervalMS", "AdvertiseIntervalMS=$advertiseIntervalMS"
    $deaArgs += "UploadThrottleBitsPS=$uploadThrottleBitsPS", "MaxConcurrentStarts=$maxConcurrentStarts", "DirectoryServerPort=$directoryServerPort"
    $deaArgs += "StreamingTimeoutMS=$streamingTimeoutMS", "StagingEnabled=$stagingEnabled", "Git=`"${gitLocation}`""
    Write-Host "Installing Windows DEA"
    [System.Diagnostics.Process]::Start("cmd", [System.String]::Join(" ", $deaArgs)).WaitForExit()
    Write-Host "Done!" -ForegroundColor Green
}

function Install{
    Write-Host "Using message bus" $messageBus
    Write-Host "Using domain" $domain
    Write-Host "Checking dependecies"


    Set-Location $env:temp | Out-Null 
    New-Item -Type Directory -Name $tempDir | Out-Null
    Set-Location $tempDir | Out-Null
    
    VerifyParameters
    #Install windows features
    CheckFeatureDependency
    #check if git is installed, if not, install it
    $gitLocation = CheckGit
    CheckZMQ

    #download and install winDEA
    InstallDEA $gitLocation
    InstallLogyard
}

function Cleanup{
    Write-Host "Cleaning up"
    #clean temporary folder used
    Remove-Item *.* -Force
    Set-Location ..
    Remove-Item $tempDir
    Set-Location $location
}

Install
Cleanup