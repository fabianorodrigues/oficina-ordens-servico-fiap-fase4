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

$cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$service = [string]$cfg.ecs.serviceName
$container = [string]$cfg.ecs.containerName
$migrationContainer = [string]$cfg.ecs.migrationContainerName
$containerPort = [int]$cfg.application.containerPort
$cluster = Ssm $cfg.aws.clusterNameParameter
$subnet1 = Ssm $cfg.ecs.privateSubnet1Parameter
$subnet2 = Ssm $cfg.ecs.privateSubnet2Parameter
$securityGroup = Ssm $cfg.ecs.taskSecurityGroupParameter
$targetGroup = Ssm $cfg.ecs.targetGroupArnParameter
$logGroup = Ssm $cfg.ecs.logGroupNameParameter
$runtimeSecret = SecretArn $cfg.secrets.runtimeDatabase
$migrationSecret = SecretArn $cfg.secrets.migrationDatabase
$desired = [int]$cfg.ecs.desiredCount
$albDns = Ssm $cfg.services.cadastroBaseUrlParameter

$baseEnv = @(Env "ASPNETCORE_ENVIRONMENT" "Production"; Env "AWS_REGION" $AwsRegion; Env "Authentication__Mode" "")
$runtimeEnv = @($baseEnv + @(
    Env "DistributedFlow__Enabled" "true",
    Env "Integrations__Cadastro__BaseUrl" "http://$albDns",
    Env "Integrations__Estoque__BaseUrl" "http://$albDns",
    Env "Messaging__Sqs__Enabled" "true",
    Env "Messaging__Sqs__Region" $AwsRegion,
    Env "Messaging__Sqs__CommandsQueueName" "oficina-estoque-comandos.fifo",
    Env "Messaging__Sqs__EventsQueueName" "oficina-ordens-eventos.fifo",
    Env "Messaging__Sqs__EventsDlqQueueName" "oficina-ordens-eventos-dlq.fifo",
    Env "Messaging__Sqs__CommandsQueueUrl" (Ssm $cfg.queues.commandsUrlParameter),
    Env "Messaging__Sqs__CommandsDlqQueueUrl" (Ssm $cfg.queues.commandsDlqUrlParameter),
    Env "Messaging__Sqs__EventsQueueUrl" (Ssm $cfg.queues.eventsUrlParameter),
    Env "Messaging__Sqs__EventsDlqQueueUrl" (Ssm $cfg.queues.eventsDlqUrlParameter),
    Env "Messaging__Sqs__ConsumerConcurrency" "$($cfg.queues.consumerConcurrency)",
    Env "Messaging__Sqs__MaxMessagesPerReceive" "$($cfg.queues.maxMessagesPerReceive)",
    Env "Messaging__Sqs__WaitTimeSeconds" "$($cfg.queues.waitTimeSeconds)",
    Env "Messaging__Sqs__VisibilityTimeoutSeconds" "$($cfg.queues.visibilityTimeoutSeconds)",
    Env "Payments__UseMock" "$($cfg.payments.useMock)".ToLowerInvariant(),
    Env "Payments__Mode" "Mock",
    Env "Payments__MockBehavior" "$($cfg.payments.mockBehavior)",
    Env "Payments__ExternalApiEnabled" "false",
    Env "Payments__ExternalWebhookEnabled" "false",
    Env "Payments__ContractStatus" "$($cfg.payments.contractStatus)"
))
$migrationEnv = @($baseEnv)
$runtimeSecrets = @(Sec "ConnectionStrings__DefaultConnection" "$($runtimeSecret):ConnectionString::")
$migrationSecrets = @(Sec "ConnectionStrings__DefaultConnection" "$($migrationSecret):ConnectionString::")
$network = "awsvpcConfiguration={subnets=[$subnet1,$subnet2],securityGroups=[$securityGroup],assignPublicIp=DISABLED}"

$migrationTaskDef = RegisterTask "$service-migration" $migrationContainer $MigrationImage $migrationEnv $migrationSecrets $false
$migrationTask = aws ecs run-task --cluster $cluster --launch-type FARGATE --started-by "$service-migration" --task-definition $migrationTaskDef --network-configuration $network --region $AwsRegion --query 'tasks[0].taskArn' --output text
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($migrationTask) -or $migrationTask -eq "None") { throw "Could not start migration task." }
aws ecs wait tasks-stopped --cluster $cluster --tasks $migrationTask --region $AwsRegion
if ($LASTEXITCODE -ne 0) { throw "Migration task wait failed." }
$exitCode = aws ecs describe-tasks --cluster $cluster --tasks $migrationTask --region $AwsRegion --query "tasks[0].containers[?name=='$migrationContainer'].exitCode | [0]" --output text
if ($exitCode -ne "0") { throw "Migration failed with exit code $exitCode." }

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
