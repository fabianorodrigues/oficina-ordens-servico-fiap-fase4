param(
    [Parameter(Mandatory = $true)][string]$RuntimeImage,
    [Parameter(Mandatory = $true)][string]$MigrationImage,
    [Parameter(Mandatory = $true)][string]$AwsRegion,
    [Parameter(Mandatory = $true)][string]$TaskExecutionRoleArn,
    [Parameter(Mandatory = $true)][string]$TaskRoleArn,
    [string]$ConfigPath = "config/official.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Ssm([string]$Name) {
    $v = aws ssm get-parameter --name $Name --region $AwsRegion --query 'Parameter.Value' --output text
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($v) -or $v -eq "None") { throw "Missing SSM parameter: $Name" }
    $v.Trim()
}

function SecretArn([string]$Name) {
    $v = aws secretsmanager describe-secret --secret-id $Name --region $AwsRegion --query 'ARN' --output text
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($v) -or $v -eq "None") { throw "Missing secret: $Name" }
    $v.Trim()
}

function Env([string]$Name, [string]$Value) { @{ name = $Name; value = $Value } }
function Sec([string]$Name, [string]$ValueFrom) { @{ name = $Name; valueFrom = $ValueFrom } }

function RegisterTask([string]$Family, [string]$Container, [string]$Image, [array]$Environment, [array]$Secrets, [bool]$ExposePort) {
    $containerDef = @{
        name = $Container
        image = $Image
        essential = $true
        environment = $Environment
        secrets = $Secrets
        logConfiguration = @{
            logDriver = "awslogs"
            options = @{
                "awslogs-group" = $logGroup
                "awslogs-region" = $AwsRegion
                "awslogs-stream-prefix" = $Family
            }
        }
    }
    if ($ExposePort) {
        $containerDef.portMappings = @(@{ containerPort = $containerPort; hostPort = $containerPort; protocol = "tcp" })
    }
    $task = @{
        family = $Family
        networkMode = "awsvpc"
        requiresCompatibilities = @("FARGATE")
        cpu = "512"
        memory = "1024"
        executionRoleArn = $TaskExecutionRoleArn
        taskRoleArn = $TaskRoleArn
        containerDefinitions = @($containerDef)
    }
    $path = Join-Path ([System.IO.Path]::GetTempPath()) "$Family-taskdef.json"
    $task | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
    $arn = aws ecs register-task-definition --cli-input-json "file://$path" --region $AwsRegion --query 'taskDefinition.taskDefinitionArn' --output text
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($arn) -or $arn -eq "None") { throw "Could not register $Family." }
    $arn.Trim()
}

function Get-ObjectProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-EcsTaskId([string]$TaskArn) {
    return (($TaskArn -split '/') | Select-Object -Last 1)
}

function AssertRdsIngressRule([string]$RdsSecurityGroupId, [string]$TaskSecurityGroupId) {
    $rulesOutput = aws ec2 describe-security-group-rules `
        --region $AwsRegion `
        --filters "Name=group-id,Values=$RdsSecurityGroupId" "Name=referenced-group-info.group-id,Values=$TaskSecurityGroupId" `
        --output json 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not validate RDS security group ingress. AWS CLI output: $(($rulesOutput | Out-String).Trim())"
        return
    }

    $rulesJson = ($rulesOutput | Out-String).Trim()
    $rules = @()
    if (-not [string]::IsNullOrWhiteSpace($rulesJson)) {
        $rules = @((ConvertFrom-Json -InputObject $rulesJson).SecurityGroupRules)
    }

    $matchingRules = @($rules | Where-Object {
        $fromPort = Get-ObjectProperty $_ "FromPort"
        $toPort = Get-ObjectProperty $_ "ToPort"
        -not [bool](Get-ObjectProperty $_ "IsEgress") `
            -and (Get-ObjectProperty $_ "IpProtocol") -eq "tcp" `
            -and $null -ne $fromPort `
            -and $null -ne $toPort `
            -and [int]$fromPort -le 1433 `
            -and [int]$toPort -ge 1433
    })

    if ($matchingRules.Count -lt 1) {
        throw "RDS security group $RdsSecurityGroupId does not allow SQL Server ingress from ECS task security group $TaskSecurityGroupId. Run the oficina-infra platform deploy to apply aws_vpc_security_group_ingress_rule.rds_from_tasks before deploying APIs."
    }

    Write-Host "RDS ingress validated: $TaskSecurityGroupId -> $RdsSecurityGroupId tcp/1433."
}

function WriteMigrationTaskDiagnostics {
    param(
        [string]$ClusterName,
        [string]$TaskArn,
        [string]$ContainerName,
        [string]$Family,
        [string]$LogGroupName
    )

    Write-Host "Collecting ECS migration diagnostics for $TaskArn"

    $taskOutput = aws ecs describe-tasks --cluster $ClusterName --tasks $TaskArn --region $AwsRegion --output json 2>&1
    if ($LASTEXITCODE -eq 0) {
        $taskJson = ($taskOutput | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($taskJson)) {
            $task = @((ConvertFrom-Json -InputObject $taskJson).tasks)[0]
            Write-Host "Task lastStatus=$(Get-ObjectProperty $task 'lastStatus') stopCode=$(Get-ObjectProperty $task 'stopCode') stoppedReason=$(Get-ObjectProperty $task 'stoppedReason')"

            $containerInfo = @($task.containers | Where-Object { (Get-ObjectProperty $_ "name") -eq $ContainerName } | Select-Object -First 1)
            if ($containerInfo.Count -gt 0) {
                $selectedContainer = $containerInfo[0]
                Write-Host "Container $ContainerName exitCode=$(Get-ObjectProperty $selectedContainer 'exitCode') reason=$(Get-ObjectProperty $selectedContainer 'reason')"
            }
        }
    }
    else {
        Write-Warning "Could not describe migration task. AWS CLI output: $(($taskOutput | Out-String).Trim())"
    }

    $taskId = Get-EcsTaskId $TaskArn
    $logStream = "$Family/$ContainerName/$taskId"
    Write-Host "CloudWatch log stream: $LogGroupName/$logStream"

    $eventsOutput = aws logs get-log-events `
        --log-group-name $LogGroupName `
        --log-stream-name $logStream `
        --limit 100 `
        --region $AwsRegion `
        --query 'events[].message' `
        --output json 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not read migration CloudWatch logs. AWS CLI output: $(($eventsOutput | Out-String).Trim())"
        return
    }

    $eventsJson = ($eventsOutput | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($eventsJson)) {
        Write-Host "No migration log events returned."
        return
    }

    $messages = @($eventsJson | ConvertFrom-Json)
    if ($messages.Count -lt 1) {
        Write-Host "No migration log events returned."
        return
    }

    Write-Host "----- Migration task log tail -----"
    foreach ($message in $messages) {
        Write-Host $message
    }
    Write-Host "----- End migration task log tail -----"
}

$cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$service = [string]$cfg.ecs.serviceName
$container = [string]$cfg.ecs.containerName
$migrationContainer = [string]$cfg.ecs.migrationContainerName
$containerPort = [int]$cfg.application.containerPort
$cluster = Ssm $cfg.aws.clusterNameParameter
$subnet1 = Ssm $cfg.ecs.privateSubnet1Parameter
$subnet2 = Ssm $cfg.ecs.privateSubnet2Parameter
$securityGroup = Ssm $cfg.ecs.taskSecurityGroupParameter
$rdsSecurityGroup = Ssm "/oficina/infra/rds/security-group-id"
$targetGroup = Ssm $cfg.ecs.targetGroupArnParameter
$logGroup = Ssm $cfg.ecs.logGroupNameParameter
$runtimeSecret = SecretArn $cfg.secrets.runtimeDatabase
$migrationSecret = SecretArn $cfg.secrets.migrationDatabase
$desired = [int]$cfg.ecs.desiredCount
$albDns = Ssm $cfg.services.cadastroBaseUrlParameter

AssertRdsIngressRule $rdsSecurityGroup $securityGroup

$baseEnv = @(
    Env "ASPNETCORE_ENVIRONMENT" "Production"
    Env "AWS_REGION" $AwsRegion
    Env "Authentication__Mode" ""
)
$runtimeEnv = @($baseEnv + @(
    Env "DistributedFlow__Enabled" "true"
    Env "Integrations__Cadastro__BaseUrl" "http://$albDns"
    Env "Integrations__Estoque__BaseUrl" "http://$albDns"
    Env "Messaging__Sqs__Enabled" "true"
    Env "Messaging__Sqs__Region" $AwsRegion
    Env "Messaging__Sqs__CommandsQueueName" "oficina-estoque-comandos.fifo"
    Env "Messaging__Sqs__EventsQueueName" "oficina-ordens-eventos.fifo"
    Env "Messaging__Sqs__EventsDlqQueueName" "oficina-ordens-eventos-dlq.fifo"
    Env "Messaging__Sqs__CommandsQueueUrl" (Ssm $cfg.queues.commandsUrlParameter)
    Env "Messaging__Sqs__CommandsDlqQueueUrl" (Ssm $cfg.queues.commandsDlqUrlParameter)
    Env "Messaging__Sqs__EventsQueueUrl" (Ssm $cfg.queues.eventsUrlParameter)
    Env "Messaging__Sqs__EventsDlqQueueUrl" (Ssm $cfg.queues.eventsDlqUrlParameter)
    Env "Messaging__Sqs__ConsumerConcurrency" "$($cfg.queues.consumerConcurrency)"
    Env "Messaging__Sqs__MaxMessagesPerReceive" "$($cfg.queues.maxMessagesPerReceive)"
    Env "Messaging__Sqs__WaitTimeSeconds" "$($cfg.queues.waitTimeSeconds)"
    Env "Messaging__Sqs__VisibilityTimeoutSeconds" "$($cfg.queues.visibilityTimeoutSeconds)"
    Env "Payments__UseMock" "$($cfg.payments.useMock)".ToLowerInvariant()
    Env "Payments__Mode" "Mock"
    Env "Payments__MockBehavior" "$($cfg.payments.mockBehavior)"
    Env "Payments__ExternalApiEnabled" "false"
    Env "Payments__ExternalWebhookEnabled" "false"
    Env "Payments__ContractStatus" "$($cfg.payments.contractStatus)"
))
$migrationEnv = @($runtimeEnv)
$runtimeSecrets = @(Sec "ConnectionStrings__DefaultConnection" "$($runtimeSecret):ConnectionString::")
$migrationSecrets = @(Sec "ConnectionStrings__DefaultConnection" "$($migrationSecret):ConnectionString::")
$network = "awsvpcConfiguration={subnets=[$subnet1,$subnet2],securityGroups=[$securityGroup],assignPublicIp=DISABLED}"

$migrationFamily = "$service-migration"
$migrationTaskDef = RegisterTask $migrationFamily $migrationContainer $MigrationImage $migrationEnv $migrationSecrets $false
$migrationTask = aws ecs run-task --cluster $cluster --launch-type FARGATE --started-by "$service-migration" --task-definition $migrationTaskDef --network-configuration $network --region $AwsRegion --query 'tasks[0].taskArn' --output text
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($migrationTask) -or $migrationTask -eq "None") { throw "Could not start migration task." }
aws ecs wait tasks-stopped --cluster $cluster --tasks $migrationTask --region $AwsRegion
if ($LASTEXITCODE -ne 0) {
    WriteMigrationTaskDiagnostics $cluster $migrationTask $migrationContainer $migrationFamily $logGroup
    throw "Migration task wait failed."
}
$exitCode = aws ecs describe-tasks --cluster $cluster --tasks $migrationTask --region $AwsRegion --query "tasks[0].containers[?name=='$migrationContainer'].exitCode | [0]" --output text
if ($exitCode -ne "0") {
    WriteMigrationTaskDiagnostics $cluster $migrationTask $migrationContainer $migrationFamily $logGroup
    throw "Migration failed with exit code $exitCode."
}

$runtimeTaskDef = RegisterTask $service $container $RuntimeImage $runtimeEnv $runtimeSecrets $true
$status = aws ecs describe-services --cluster $cluster --services $service --region $AwsRegion --query 'services[0].status' --output text 2>$null
if ($LASTEXITCODE -eq 0 -and $status -eq "ACTIVE") {
    aws ecs update-service --cluster $cluster --service $service --task-definition $runtimeTaskDef --desired-count $desired --region $AwsRegion | Out-Null
}
else {
    aws ecs create-service --cluster $cluster --service-name $service --task-definition $runtimeTaskDef --desired-count $desired --launch-type FARGATE --network-configuration $network --load-balancers "targetGroupArn=$targetGroup,containerName=$container,containerPort=$containerPort" --health-check-grace-period-seconds 60 --region $AwsRegion | Out-Null
}
aws ecs wait services-stable --cluster $cluster --services $service --region $AwsRegion
if ($LASTEXITCODE -ne 0) { throw "Service did not become stable." }
$healthy = aws elbv2 describe-target-health --target-group-arn $targetGroup --region $AwsRegion --query 'length(TargetHealthDescriptions[?TargetHealth.State==`healthy`])' --output text
if ($LASTEXITCODE -ne 0 -or [int]$healthy -lt 1) { throw "No healthy target found." }
Write-Host "ECS deployment completed for $service."
