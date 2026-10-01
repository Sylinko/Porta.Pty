param(
    [Parameter(Mandatory)][DateTimeOffset]$RunCreatedAt,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$RunNumber
)

# Original run creation time keeps the version stable across jobs and reruns.
"999.$($RunCreatedAt.UtcDateTime.ToString('yyyyMMdd')).$RunNumber"
