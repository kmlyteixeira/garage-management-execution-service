# Execution Service

Gerencia a fila de execução, diagnóstico, reparo e finalização. O MongoDB é
privado deste serviço e guarda documentos de execução; o serviço não consulta
PostgreSQL do OS ou do billing.

Endpoints: `POST /executions`, `PUT /executions/{id}/status` e `GET /health`.
A conclusão publica `ExecutionCompleted` com o mesmo `correlationId` da Saga.
As dependências locais podem ser iniciadas com
`docker compose -f ../garage-management-api-dotnet/docker-compose.fase4.yml up -d`.
