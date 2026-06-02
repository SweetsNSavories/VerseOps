$ErrorActionPreference = 'Continue'
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = 'C:\Users\pravth\Downloads\VerseOps\VerseOps'
$traceDir = Join-Path $root 'tools\demo'
$trace   = Join-Path $traceDir "lifecycle-trace.log"
$envName = "verseops-demo-$ts"
$domain  = "verseops-demo-$ts"  # max 32 chars; sticking under
$region  = 'unitedstates'

function W($msg) {
  $line = "[$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ'))] $msg"
  $line | Out-Host
  Add-Content -Path $trace -Value $line
}

if (-not (Test-Path $traceDir)) { New-Item -ItemType Directory -Force -Path $traceDir | Out-Null }
"=== VerseOps API Explorer lifecycle suite | $([DateTime]::UtcNow.ToString('u')) ===" | Set-Content $trace
W "Tenant=pfecrmonline.onmicrosoft.com  User=pthonda@pfecrmonline.onmicrosoft.com  ProfileIdx=4 (nyctest active)"
W "New env display name: $envName"
W "Region=$region  Currency=USD  Language=1033 (en-US)  Type=Sandbox"

# 1. CREATE
W ""
W "=== STEP 1: pac admin create ==="
$createOut = pac admin create --name $envName --region $region --type Sandbox --currency USD --language 1033 --domain $domain 2>&1 | Out-String
$createOut | Add-Content $trace
W "create exit=$LASTEXITCODE"

# Extract the new EnvironmentId from the trace
$envId = $null
$m = [regex]::Match($createOut, "Environment Id\s*:\s*([0-9a-fA-F\-]{36})")
if ($m.Success) { $envId = $m.Groups[1].Value; W "ENV_ID=$envId" }
else { W "Could not parse Environment Id; falling back to lookup by name"; }

# Lookup by name if extraction failed
if (-not $envId) {
  $listOut = pac admin list --json 2>&1 | Out-String
  try {
    $envs = $listOut | ConvertFrom-Json
    $match = $envs | Where-Object { $_.DisplayName -eq $envName } | Select-Object -First 1
    if ($match) { $envId = $match.EnvironmentId; W "Resolved ENV_ID=$envId via list lookup" }
  } catch { W "list parse failed: $_" }
}

if (-not $envId) { W "ABORT: could not resolve new env id"; exit 1 }

# 2. STATUS (no extra polling — pac admin create already blocks until provisioned)
W ""
W "=== STEP 2: pac admin status (current in-flight ops) ==="
pac admin status 2>&1 | Add-Content $trace
W "status exit=$LASTEXITCODE"

# 3. LIST-BACKUPS (should be empty initially)
W ""
W "=== STEP 3: pac admin list-backups (pre-backup) ==="
pac admin list-backups --environment $envId 2>&1 | Add-Content $trace
W "list-backups exit=$LASTEXITCODE"

# 4. BACKUP
W ""
W "=== STEP 4: pac admin backup ==="
$backupLabel = "verseops-backup-$ts"
pac admin backup --environment $envId --label $backupLabel 2>&1 | Add-Content $trace
W "backup exit=$LASTEXITCODE  label=$backupLabel"

# 5. LIST-BACKUPS (should show new one)
W ""
W "=== STEP 5: pac admin list-backups (post-backup) ==="
pac admin list-backups --environment $envId 2>&1 | Add-Content $trace
W "list-backups exit=$LASTEXITCODE"

# 6. SET-RUNTIME-STATE → AdminMode
W ""
W "=== STEP 6: pac admin set-runtime-state AdminMode ==="
pac admin set-runtime-state --environment $envId --runtime-state AdminMode 2>&1 | Add-Content $trace
W "set-runtime-state AdminMode exit=$LASTEXITCODE"

# 7. SET-RUNTIME-STATE → Enabled (off admin mode)
W ""
W "=== STEP 7: pac admin set-runtime-state Enabled ==="
pac admin set-runtime-state --environment $envId --runtime-state Enabled 2>&1 | Add-Content $trace
W "set-runtime-state Enabled exit=$LASTEXITCODE"

# 8. SET-GOVERNANCE-CONFIG → enable managed env
W ""
W "=== STEP 8: pac admin set-governance-config enable Managed Environment ==="
pac admin set-governance-config --environment $envId --protection-level Standard 2>&1 | Add-Content $trace
W "managed-env enable exit=$LASTEXITCODE"

# 9. SET-GOVERNANCE-CONFIG → disable managed env
W ""
W "=== STEP 9: pac admin set-governance-config disable Managed Environment (Basic = disabled) ==="
pac admin set-governance-config --environment $envId --protection-level Basic 2>&1 | Add-Content $trace
W "managed-env disable exit=$LASTEXITCODE"

# 10. LIST-ROLES (verifies role discovery)
W ""
W "=== STEP 10: pac admin list-roles ==="
pac admin list-roles --environment $envId 2>&1 | Add-Content $trace
W "list-roles exit=$LASTEXITCODE"

# 11. RESET (Dataverse reset of brand-new env — fast, no data to wipe)
W ""
W "=== STEP 11: pac admin reset ==="
pac admin reset --environment $envId --currency USD --language 1033 --domain "${domain}r" --name "${envName}-reset" 2>&1 | Add-Content $trace
W "reset exit=$LASTEXITCODE"

# 12. COPY command captured but NOT executed (a full copy takes 30+ minutes — show syntax for demo)
W ""
W "=== STEP 12: pac admin copy (command captured, NOT executed) ==="
$copyCmd = "pac admin copy --source-env $envId --target-env <SANDBOX_ENV_ID> --type FullCopy --target-name `"${envName}-copy`""
W "Demo command:  $copyCmd"
W "(Skipped at runtime: FullCopy of a freshly-provisioned sandbox typically takes 30+ minutes; MinimalCopy ~10 min. Run live during the customer demo with two pre-created sandboxes.)"

# 13. DELETE
W ""
W "=== STEP 13: pac admin delete (cleanup) ==="
pac admin delete --environment $envId 2>&1 | Add-Content $trace
W "delete exit=$LASTEXITCODE"

W ""
W "=== SUITE COMPLETE | env_id=$envId | trace=$trace ==="
