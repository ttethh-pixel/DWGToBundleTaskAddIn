param([Parameter(Mandatory=$true)][string]$Dwg,[string]$Pdf,[string]$AcConsole='D:\Program Files\AutoCAD\AutoCAD 2020\accoreconsole.exe',[string]$Gs='C:\Program Files\PDFCreator\Ghostscript\bin\gswin32c.exe')
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $Dwg)){throw "DWG not found: $Dwg"}
$Dwg=(Resolve-Path -LiteralPath $Dwg).Path
$dir=Split-Path -Parent $Dwg; $base=[IO.Path]::GetFileNameWithoutExtension($Dwg)
$colorPdf=Join-Path $dir ($base+'.pdf')   # output of DWG_TO_PDF
if(-not $Pdf){$Pdf=Join-Path $dir ($base+'_mono.pdf')}
if(-not(Test-Path -LiteralPath $AcConsole)){throw "accoreconsole not found: $AcConsole"}
if(-not(Test-Path -LiteralPath $Gs)){throw "gswin not found: $Gs"}
$scr=Join-Path $dir ($base+'.run.tmp.scr')
$dll=Join-Path $PSScriptRoot 'SheetLayoutDistributor\bin\Release\net48\SheetLayoutDistributor.dll'
@('SECURELOAD','0','FILEDIA','0','CMDDIA','0','NETLOAD',$dll,'DWG_TO_PDF','QUIT','Y')|Set-Content -LiteralPath $scr -Encoding ASCII
$env:SLD_HEADLESS='1'
Remove-Item -LiteralPath $colorPdf,$Pdf -Force -EA SilentlyContinue
Write-Host 'Step 1/2: AutoCAD -> color PDF (crop to frame)...'
& $AcConsole '/i' $Dwg '/s' $scr '/l' 'en-US'|Out-Null
Remove-Item -LiteralPath $scr -Force -EA SilentlyContinue
if(-not(Test-Path -LiteralPath $colorPdf)){throw 'Color PDF not created (check plugin logs\export_log.txt).'}
Write-Host 'Step 2/2: Ghostscript -> mono (DeviceGray)...'
$gsOut=$Pdf; if([IO.Path]::GetFullPath($Pdf) -ieq [IO.Path]::GetFullPath($colorPdf)){$gsOut=$colorPdf+'.gray.tmp'}
$gsArgs=@('-dNOPAUSE','-dBATCH','-o',$gsOut,'-sDEVICE=pdfwrite','-dProcessColorModel=/DeviceGray','-dColorConversionStrategy=/Gray','-dOverrideICC=true',$colorPdf)
& $Gs @gsArgs
if($gsOut -ne $Pdf){Move-Item -LiteralPath $gsOut -Destination $Pdf -Force}
Remove-Item -LiteralPath $colorPdf -Force -EA SilentlyContinue
if(Test-Path -LiteralPath $Pdf){Write-Host ("Done: {0} ({1} bytes)" -f $Pdf,(Get-Item -LiteralPath $Pdf).Length)}else{throw 'Mono PDF not created.'}
