$ErrorActionPreference = 'Continue'
$envId = 'f7b68b32-5369-ea6d-bf9c-9e5b52234018'
$envName = 'verseops-demo-20260601-225042'
$ts = '20260601-225042'
$trace = 'C:\Users\pravth\Downloads\VerseOps\VerseOps\tools\demo\lifecycle-trace.log'
function W($msg) { $l = "[$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ'))] $msg"; $l | Out-Host; Add-Content -Path $trace -Value $l }
W ''
W "=== RESUME ENV_ID=$envId  (DV ready) ==="

# 3. LIST-BACKUPS (pre-backup, now that DV is ready)
W ''
W '=== STEP 3 (retry): pac admin list-backups (pre-backup) ==='
pac admin list-backups --environment $envId 2>&1 | Add-Content $trace
W "list-backups exit=$LASTEXITCODE"

# 4. BACKUP
W ''
W '=== STEP 4 (retry): pac admin backup ==='
$backupLabel = "verseops-backup-$ts"
pac admin backup --environment $envId --label $backupLabel 2>&1 | Add-Content $trace
W "backup exit=$LASTEXITCODE  label=$backupLabel"

# 5. LIST-BACKUPS (post)
W ''
W '=== STEP 5: pac admin list-backups (post-backup) ==='
pac admin list-backups --environment $envId 2>&1 | Add-Content $trace
W "list-backups exit=$LASTEXITCODE"

# 6. SET-RUNTIME-STATE AdminMode
W ''
W '=== STEP 6: pac admin set-runtime-state AdminMode ==='
pac admin set-runtime-state --environment $envId --runtime-state AdminMode 2>&1 | Add-Content $trace
W "set-runtime-state AdminMode exit=$LASTEXITCODE"

# 7. SET-RUNTIME-STATE Enabled
W ''
W '=== STEP 7: pac admin set-runtime-state Enabled ==='
pac admin set-runtime-state --environment $envId --runtime-state Enabled 2>&1 | Add-Content $trace
W "set-runtime-state Enabled exit=$LASTEXITCODE"

# 8. ENABLE MANAGED ENV
W ''
W '=== STEP 8: pac admin set-governance-config Standard (enable Managed Env) ==='
pac admin set-governance-config --environment $envId --protection-level Standard 2>&1 | Add-Content $trace
W "managed-env Standard exit=$LASTEXITCODE"

# 9. DISABLE MANAGED ENV
W ''
W '=== STEP 9: pac admin set-governance-config Basic (disable Managed Env) ==='
pac admin set-governance-config --environment $envId --protection-level Basic 2>&1 | Add-Content $trace
W "managed-env Basic exit=$LASTEXITCODE"

# 10. LIST-ROLES
W ''
W '=== STEP 10: pac admin list-roles ==='
pac admin list-roles --environment $envId 2>&1 | Add-Content $trace
W "list-roles exit=$LASTEXITCODE"

# 11. RESET
W ''
W '=== STEP 11: pac admin reset ==='
pac admin reset --environment $envId --currency USD --language 1033 --name "${envName}-reset" 2>&1 | Add-Content $trace
W "reset exit=$LASTEXITCODE"

# 12. COPY captured but not executed
W ''
W '=== STEP 12: pac admin copy (captured, NOT executed) ==='
W "Demo command:  pac admin copy --source-env $envId --target-env <TARGET_SANDBOX_ID> --type FullCopy --name `"${envName}-copy`""

# 13. DELETE
W ''
W '=== STEP 13: pac admin delete (cleanup) ==='
pac admin delete --environment $envId 2>&1 | Add-Content $trace
W "delete exit=$LASTEXITCODE"

W ''
W '=== SUITE COMPLETE ==='
