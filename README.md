# Execution Service

## CI/CD em `feat/development`

A [esteira de CI/CD](.github/workflows/ci.yml) valida pushes na branch e pull requests direcionados a ela. O GitHub Actions compila em Release com avisos tratados como erros e disponibiliza os relatórios TRX e Cobertura XML em **Artifacts → test-results-unit**.

A esteira inicia a API publicada com MongoDB e RabbitMQ efêmeros, confirma `200 Healthy` em `/health` e publica um evento `billing.aprovado`. A consulta HTTP precisa retornar a execução gravada com status `Pendente`.

Após as verificações, os pushes na branch geram o artefato `execution-service-feat-development-<SHA>`, disponível por 30 dias. Ele contém a aplicação publicada e o checksum SHA256 do pacote.

## Implantar o pacote

1. Baixe o artefato da execução bem-sucedida no GitHub Actions e extraia o ZIP. Valide o checksum e extraia a aplicação em um diretório de release.

   ```bash
   sha256sum --check execution-service-feat-development.tar.gz.sha256 &&
     mkdir execution-release &&
     tar -xzf execution-service-feat-development.tar.gz -C execution-release
   ```

2. No destino, disponibilize o runtime ASP.NET Core 10 e configure `MONGODB_CONNECTION_STRING` e `MONGODB_DATABASE`. Para participar da Saga, defina `RABBITMQ_ENABLED=true` e configure `RABBITMQ_HOST`, `RABBITMQ_PORT`, `RABBITMQ_USER`, `RABBITMQ_PASSWORD` e `RABBITMQ_VHOST` com os valores do ambiente. Configure `ASPNETCORE_URLS` para a porta do serviço.
3. Inicie a release com `dotnet TechChallenge.Execution.Api.dll` no diretório extraído. Verifique `200 Healthy` em `/health` e confirme que o consumidor está ativo na fila `execution-service.events` quando a mensageria estiver habilitada.
