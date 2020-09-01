param(
 [Parameter(Mandatory=$True)]
 [string]
 $resourceGroupName,

 [string]
 $resourceGroupLocation,

 [Parameter(Mandatory=$True)]
 [string]
 $serviceNamePrefix
)


#Create or check for existing resource group
$resourceGroup = Get-AzResourceGroup -Name $resourceGroupName -ErrorAction SilentlyContinue
if(!$resourceGroup)
{
    Write-Host "Creating resource group '$resourceGroupName' in location '$resourceGroupLocation'";
    New-AzResourceGroup -Name $resourceGroupName -Location $resourceGroupLocation
}
else
{
    Write-Host "Using existing resource group '$resourceGroupName'";
}

$serviceBusNameSpace = $serviceNamePrefix + "-sb-namespace"
$serviceBusQueue = "demoqueue"
$appServicePlan = $serviceNamePrefix + "-appsvcplan"
$appName = $serviceNamePrefix + "-webapp"
$storageAcctName = $serviceNamePrefix + "storage"
$storageContainerName = $serviceNamePrefix + "container"
$webjobName = "DemoWebJob"

# Start the deployment
Write-Host "Starting deployment...";
#####################################################
# Create the Azure Resources 
#####################################################
if($null -eq (Get-AzServiceBusNamespace -ResourceGroupName $resourceGroupName -Name $serviceBusNameSpace -ErrorAction SilentlyContinue))
{
    Write-Host "Creating Service Bus Namespace: $($serviceBusNameSpace)"
    New-AzServiceBusNamespace -ResourceGroupName $resourceGroupName -Name $serviceBusNameSpace -Location $resourceGroupLocation -SkuName  "Standard" 
}

if($null -eq (Get-AzServiceBusQueue -ResourceGroupName $resourceGroupName -Namespace $serviceBusNameSpace -Name serviceBusQueue -ErrorAction SilentlyContinue))
{
    Write-Host "Creating Service Bus Queue: $($serviceBusQueue)"
    New-AzServiceBusQueue -ResourceGroupName $resourceGroupName -Name $serviceBusQueue -Namespace $serviceBusNameSpace
}

if($null -eq (Get-AzAppServicePlan -ResourceGroupName $resourceGroupName -Name $appServicePlan -ErrorAction SilentlyContinue))
{
    Write-Host "Creating App Service Plan: $($appServicePlan)"
    New-AzAppServicePlan -ResourceGroupName $resourceGroupName -Name $appServicePlan -Location $resourceGroupLocation -Tier "Basic" -NumberofWorkers 1  -WorkerSize "Small"
}

if($null -eq (Get-AzWebApp -ResourceGroupName $resourceGroupName -Name $appName -ErrorAction SilentlyContinue))
{
    Write-Host "Creating App Service Web app: $($appServicePlan)"
    New-AzWebApp -ResourceGroupName $resourceGroupName -Name $appName -Location $resourceGroupLocation -AppServicePlan $appServicePlan
}

if($null -eq (Get-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAcctName -ErrorAction SilentlyContinue))
{
    Write-Host "Creating Storage account:  $($storageAcctName)"
    New-AzStorageAccount -ResourceGroupName $resourceGroupName -AccountName $storageAcctName -Location $resourceGroupLocation -SkuName "Standard_LRS"
}

if($null -eq (Get-AzRmStorageContainer -ResourceGroupName $resourceGroupName -StorageAccountName $storageAcctName -Name $storageContainerName -ErrorAction SilentlyContinue))
{
    Write-Host "Creating Storage container:  $($storageContainerName)"
    New-AzRmStorageContainer -ResourceGroupName $resourceGroupName -StorageAccountName $storageAcctName -Name $storageContainerName
}
Write-Host "Deployment Complete";

$storeagekey = (Get-AzStorageAccountKey -ResourceGroupName $resourceGroupName -Name $storageAcctName)[0].Value
$sbString = (Get-AzServiceBusKey -ResourceGroupName $resourceGroupName -Namespace $serviceBusNameSpace -Name "RootManageSharedAccessKey").PrimaryConnectionString
$storageString = "DefaultEndpointsProtocol=https;AccountName=$($storageAcctName);AccountKey=$($storeagekey)"
$sbAppSettings = """AzureWebJobsServiceBus"":""$($sbString)"""

#####################################################
# Set Azure App settings values for webjob
#####################################################
Write-Host "Updating App Service app settings";
$tmp = (Get-AzWebApp -ResourceGroupName $resourceGroupName -Name $appName).SiteConfig.AppSettings
$appSettings = @{}
ForEach ($item in $tmp) 
{
    $appSettings[$item.Name] = $item.Value
}

$appSettings.AzureWebJobsDashboard = $storageString
$appSettings.AzureWebJobsStorage = $storageString
Set-AzWebApp -ResourceGroupName $resourceGroupName -Name $appName -AppSettings $appSettings

#####################################################
# Updating project configuration files 
#####################################################
Write-Host "Updating project configuration files";
"{ $($sbAppSettings) }" | Out-File -FilePath (Resolve-Path "./ServiceBusWebJobCore/appsettings.json").Path

$appConfigFile = (Resolve-Path "./ServiceBusUtility/app.config").Path
[xml]$appCfg = Get-Content -Path $appConfigFile

($appCfg.configuration.appSettings.add | ? {$_.key -eq "Microsoft.ServiceBus.ConnectionString" } ).value  = $sbString
($appCfg.configuration.appSettings.add | ? {$_.key -eq "Microsoft.ServiceBus.QueueName" }).value = $serviceBusQueue
$appCfg.Save($appConfigFile)


#####################################################
# Building projects
#####################################################
Write-Host "Building projects";
dotnet restore "ServiceBusExample.sln" 
dotnet build "ServiceBusExample.sln" --configuration Debug
$publishFolder = (Resolve-Path ".").Path +"\ServiceBusWebJobCore\publish"
dotnet publish ".\ServiceBusWebJobCore\ServiceBusWebJobCore.csproj" -c Debug -o $publishFolder

$webJobZip = (Resolve-Path ".").Path + "\webjob.zip"
Write-Host $webJobZip -ForegroundColor Green
if(Test-path $webJobZip) 
{
    Remove-item $webJobZip
}
Add-Type -assembly "system.io.compression.filesystem"
[io.compression.zipfile]::CreateFromDirectory($publishFolder, $webJobZip)

#####################################################
# Deploying WebJob code to Azure Azure App Service
#####################################################
Write-Host "Deploying web job";
[xml]$profileXml = Get-AzWebAppPublishingProfile -ResourceGroupName $resourceGroupName -Name $appName 
$msdeploy = $profileXml.publishData.publishProfile | ? {$_.publishMethod -eq "MSDeploy" }

$creds = "$($msdeploy.userName):$($msdeploy.userPWD)"
$encodedCreds = [System.Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes($creds))
$basicAuthValue = "Basic $encodedCreds"

$ZipHeaders = @{
    Authorization = $basicAuthValue
    "Content-Disposition" = "attachment; filename=ServiceBusWebJobCore.exe"
    "Content-Type" = "application/zip"
}
Invoke-WebRequest -Uri https://$appName.scm.azurewebsites.net/api/continuouswebjobs/$webjobName -Headers $ZipHeaders -InFile $webJobZip -ContentType "application/zip" -Method Put

#####################################################
# Output the values for reference
#####################################################
Write-Output "These are for reference only, they have already been added..."

Write-Output "For appsettings.json:"
Write-Host """AzureWebJobsServiceBus"":""$($sbString)""" -ForegroundColor "Green"
Write-Output ""

Write-Output "For App Service settings:"
Write-Host """AzureWebJobsServiceBus"":""$($sbString)""" -ForegroundColor "Green"
Write-Host """AzureWebJobsDashboard"":""$($storageString)""" -ForegroundColor "Green"
Write-Host """AzureWebJobsStorage"":""$($storageString)""" -ForegroundColor "Green"
