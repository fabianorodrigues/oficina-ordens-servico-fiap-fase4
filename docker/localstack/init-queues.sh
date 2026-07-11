#!/usr/bin/env sh
set -eu

endpoint="${LOCALSTACK_ENDPOINT:-http://localstack:4566}"
region="${AWS_REGION:-us-east-1}"

aws_cmd() {
  aws --endpoint-url "$endpoint" --region "$region" "$@"
}

create_fifo_queue() {
  name="$1"
  dlq_arn="${2:-}"
  if [ -n "$dlq_arn" ]; then
    attrs_file="/tmp/${name}.json"
    cat > "$attrs_file" <<EOF
{"FifoQueue":"true","ContentBasedDeduplication":"false","RedrivePolicy":"{\"deadLetterTargetArn\":\"$dlq_arn\",\"maxReceiveCount\":\"3\"}"}
EOF
  else
    attrs_file="/tmp/${name}.json"
    cat > "$attrs_file" <<EOF
{"FifoQueue":"true","ContentBasedDeduplication":"false"}
EOF
  fi

  aws_cmd sqs create-queue --queue-name "$name" --attributes "file://$attrs_file" >/dev/null
}

until aws_cmd sqs list-queues >/dev/null 2>&1; do
  echo "Aguardando LocalStack SQS..."
  sleep 2
done

create_fifo_queue "oficina-estoque-comandos-dlq.fifo"
create_fifo_queue "oficina-ordens-eventos-dlq.fifo"

estoque_dlq_arn=$(aws_cmd sqs get-queue-attributes --queue-url "$(aws_cmd sqs get-queue-url --queue-name oficina-estoque-comandos-dlq.fifo --query QueueUrl --output text)" --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)
ordens_dlq_arn=$(aws_cmd sqs get-queue-attributes --queue-url "$(aws_cmd sqs get-queue-url --queue-name oficina-ordens-eventos-dlq.fifo --query QueueUrl --output text)" --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)

create_fifo_queue "oficina-estoque-comandos.fifo" "$estoque_dlq_arn"
create_fifo_queue "oficina-ordens-eventos.fifo" "$ordens_dlq_arn"

aws_cmd sqs list-queues
