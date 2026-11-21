@{
    RootModule = 'UniBackup.psm1'
    ModuleVersion = '2.0.0'
    GUID = '8cb6a2df-9f37-4d7a-85b4-d54b6703b412'
    Author = 'UniBackup Pro Team'
    CompanyName = 'UniBackup Pro'
    Copyright = '(c) 2026 UniBackup Pro. All rights reserved.'
    Description = 'PowerShell module for managing, scheduling, running, and exporting UniBackup Pro enterprise cloud backups.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Get-UBJob', 'Start-UBJob', 'Stop-UBRun', 'Get-UBHistory', 'Export-UBConfig', 'Import-UBConfig')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
}
