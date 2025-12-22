$root = Get-Location
$serverDll = "$root\collab-dotnet-server\bin\Debug\net6.0\collab-dotnet-server.dll"
$clientDll = "$root\collab-dotnet-client\bin\Debug\net6.0\collab-dotnet-client.dll"
$devAiExe = "$root\DevAi\bin\Debug\DevAi.exe"

$ws1 = "$root\demo_workspaces\client1"
$ws2 = "$root\demo_workspaces\client2"

New-Item -ItemType Directory -Force -Path $ws1
New-Item -ItemType Directory -Force -Path $ws2

Write-Host "Starting Server..."
Start-Process dotnet -ArgumentList "$serverDll --urls http://localhost:5000" -WorkingDirectory "$root\collab-dotnet-server"

Start-Sleep -Seconds 5

Write-Host "Starting Client 1..."
Start-Process dotnet -ArgumentList "$clientDll http://localhost:5000 demo" -WorkingDirectory $ws1

Write-Host "Starting Client 2..."
Start-Process dotnet -ArgumentList "$clientDll http://localhost:5000 demo" -WorkingDirectory $ws2

Write-Host "Starting DevAi 1..."
Start-Process $devAiExe -WorkingDirectory $ws1

Write-Host "Starting DevAi 2..."
Start-Process $devAiExe -WorkingDirectory $ws2
