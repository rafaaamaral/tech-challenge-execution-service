# Execution Service

## CI/CD na `main`

A [esteira de CI/CD](.github/workflows/ci.yml) roda em pushes e pull requests para a `main` e pode ser iniciada manualmente em **Actions**. O GitHub Actions compila em Release com avisos tratados como erros e disponibiliza os relatórios TRX, Cobertura XML e OpenCover em **Artifacts → test-results-unit**.

A esteira inicia a API publicada com MongoDB e RabbitMQ efêmeros, confirma `200 Healthy` em `/health` e publica um evento `billing.aprovado`. A consulta HTTP precisa retornar a execução gravada com status `Pendente`.

O job **Validar imagem em Kubernetes** constrói a imagem do `Dockerfile`, sobe um cluster temporário com kind e aplica [`k8s/execution.yaml`](k8s/execution.yaml). O job aguarda o rollout da API e do MongoDB e verifica que o Service responde `Healthy` em `/health`.

Execuções na `main` que passam pelas verificações e pelo Quality Gate do SonarQube geram o artefato `execution-service-main-<SHA>`, disponível por 30 dias. Ele contém a aplicação publicada e o checksum SHA256 do pacote.

## SonarQube

O job **SonarQube** analisa o código C# com o SonarScanner for .NET, importa a cobertura OpenCover dos testes da esteira e aguarda o Quality Gate. Ele é obrigatório na `main` e também roda em pull requests do próprio repositório quando `SONAR_PROJECT_KEY` está configurada. Pull requests de forks e do Dependabot executam as demais verificações.

1. Crie ou importe o projeto no SonarQube. No Cloud, selecione análise via GitHub Actions e desative a análise automática se ela estiver ativa.
2. Em **Settings → Secrets and variables → Actions** no GitHub, cadastre o secret `SONAR_TOKEN` com permissão **Execute Analysis** para o projeto.
3. Em **Variables**, cadastre `SONAR_ORGANIZATION` para SonarQube Cloud, ou `SONAR_HOST_URL` para um SonarQube Server acessível pelo runner. Para uma organização Cloud na região dos EUA, defina também `SONAR_REGION=us`.
4. Cadastre em `SONAR_PROJECT_KEY` a chave do projeto e confirme que a branch principal está configurada como `main` no SonarQube. Consulte o resultado do Quality Gate e o link da análise no log do job.

Para analisar pull requests, use uma edição ou plano com suporte a essa análise. Em um projeto Cloud novo, faça primeiro a análise da `main`. [Planos do SonarQube Cloud](https://docs.sonarsource.com/sonarqube-cloud/administering-sonarcloud/managing-subscription/subscription-plans) · [Primeira análise no Cloud](https://docs.sonarsource.com/sonarqube-cloud/getting-started/first-analysis)

## Implantar o pacote

1. Baixe o artefato da execução bem-sucedida no GitHub Actions e extraia o ZIP. Valide o checksum e extraia a aplicação em um diretório de release.

   ```bash
   sha256sum --check execution-service-main.tar.gz.sha256 &&
     mkdir execution-release &&
     tar -xzf execution-service-main.tar.gz -C execution-release
   ```

2. No destino, disponibilize o runtime ASP.NET Core 10 e configure `MONGODB_CONNECTION_STRING` e `MONGODB_DATABASE`. Para participar da Saga, defina `RABBITMQ_ENABLED=true` e configure `RABBITMQ_HOST`, `RABBITMQ_PORT`, `RABBITMQ_USER`, `RABBITMQ_PASSWORD` e `RABBITMQ_VHOST` com os valores do ambiente. Configure `ASPNETCORE_URLS` para a porta do serviço.
3. Inicie a release com `dotnet TechChallenge.Execution.Api.dll` no diretório extraído. Verifique `200 Healthy` em `/health` e confirme que o consumidor está ativo na fila `execution-service.events` quando a mensageria estiver habilitada.
