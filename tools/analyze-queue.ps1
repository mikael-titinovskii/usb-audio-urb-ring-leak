param(
  [Parameter(Mandatory=$true)][string]$TracePath,
  [ValidatePattern('^[A-Za-z0-9-]*$')][string]$ReportLabel='',
  [string]$AnalysisRoot=(Join-Path $env:LOCALAPPDATA 'FocusriteDiagnostics\AnalysisTools')
)
$ErrorActionPreference='Stop'
$analysisRoot=$AnalysisRoot
# The analyzer needs these assemblies staged under $AnalysisRoot from the NuGet
# packages shown; load them by explicit path because they are not in the GAC.
$requiredAssemblies=@(
  @{ Package='System.Runtime.CompilerServices.Unsafe'; Version='6.1.2'; Path='System.Runtime.CompilerServices.Unsafe.6.1.2\lib\net462\System.Runtime.CompilerServices.Unsafe.dll' },
  @{ Package='System.Memory'; Version='4.5.5'; Path='System.Memory.4.5.5\lib\net461\System.Memory.dll' },
  @{ Package='System.Buffers'; Version='4.5.1'; Path='System.Buffers.4.5.1\lib\net461\System.Buffers.dll' },
  @{ Package='TraceEvent'; Version='3.2.6'; Path='TraceEvent.3.2.6\lib\netstandard2.0\Microsoft.Diagnostics.Tracing.TraceEvent.dll' }
)
$missing=@($requiredAssemblies | Where-Object { -not (Test-Path -LiteralPath (Join-Path $analysisRoot $_.Path)) })
if($missing.Count){
  $needed=($missing | ForEach-Object { "$($_.Package) $($_.Version)" }) -join ', '
  throw "Analysis assemblies missing under '$analysisRoot': $needed. The required NuGet packages are System.Runtime.CompilerServices.Unsafe 6.1.2, System.Memory 4.5.5, System.Buffers 4.5.1 and TraceEvent 3.2.6; stage them under that folder and pass -AnalysisRoot if it lives elsewhere."
}
$unsafeLoaded=[Reflection.Assembly]::LoadFrom((Join-Path $analysisRoot 'System.Runtime.CompilerServices.Unsafe.6.1.2\lib\net462\System.Runtime.CompilerServices.Unsafe.dll'))
$resolver=[ResolveEventHandler]{param($sender,$eventArgs) if($eventArgs.Name -like 'System.Runtime.CompilerServices.Unsafe,*'){return $unsafeLoaded};return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
[Reflection.Assembly]::LoadFrom((Join-Path $analysisRoot 'System.Memory.4.5.5\lib\net461\System.Memory.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $analysisRoot 'System.Buffers.4.5.1\lib\net461\System.Buffers.dll')) | Out-Null
$traceAssembly=Join-Path $analysisRoot 'TraceEvent.3.2.6\lib\netstandard2.0\Microsoft.Diagnostics.Tracing.TraceEvent.dll'
$netstandard=Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard\v4.0_2.0.0.0__cc7b13ffcd2ddd51\netstandard.dll'
[Reflection.Assembly]::LoadFrom($traceAssembly) | Out-Null
Add-Type -Path (Join-Path $PSScriptRoot 'AnalyzeFocusriteUsbQueue.cs') -ReferencedAssemblies @($traceAssembly,$netstandard,'System.Core.dll')
$suffix=if($ReportLabel){'.usb-queue-'+$ReportLabel}else{'.usb-queue'}
$reportPath=[IO.Path]::ChangeExtension($TracePath,$suffix+'.txt')
$csvPath=[IO.Path]::ChangeExtension($TracePath,$suffix+'.csv')
if(Test-Path -LiteralPath $reportPath){throw 'Analysis exists; not overwritten.'}
$report=[FocusriteDiagnostics.UsbQueueTraceAnalysis]::Analyze($TracePath,$csvPath)
$report | Out-File -LiteralPath $reportPath -Encoding utf8
$report
