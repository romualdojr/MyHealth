# Módulo de integração HealthKit (iPhone)

Este diretório contém uma implementação nativa isolada. Ainda não há app iOS nem API nesta pasta para conectá-la ao fluxo real. O arquivo Swift precisa entrar em um target iOS existente e ser compilado/testado no Xcode com um iPhone físico e fontes reais de dados.

## Configuração no target iOS

1. Habilitar a capability **HealthKit** no Xcode (entitlement `com.apple.developer.healthkit`).
2. Incluir `NSHealthShareUsageDescription`: “Ler sua frequência cardíaca do Apple Saúde durante os treinos para mostrar sua evolução e sincronizá-la com sua conta.”
3. Incluir `NSHealthUpdateUsageDescription`: “Salvar no Apple Saúde os treinos concluídos quando você autorizar.”
4. Exibir explicação de uso e compartilhamento com a API antes de iniciar a conexão; chamar `start` após toque explícito do aluno. O diálogo de leitura é controlado pelo iOS.
5. Adicionar botão/controle separado para salvar treinos no Apple Saúde; chamar `saveToAppleHealth` somente após essa escolha. A permissão de escrita é independente da leitura.

## Uso no fluxo do app

```swift
let healthKit = HealthKitWorkoutIntegration()
let workoutID = UUID() // Use o ID estável da sessão no backend.
let uploader = HTTPWorkoutHeartRateUploader(
    endpoint: authenticatedWorkoutURL,
    bearerToken: currentAccessToken
)

// Ação de usuário: iniciar treino.
try await healthKit.start(workoutID: workoutID) { points in
    // Atualizar gráfico e último valor recebido. Os dados podem chegar com atraso.
}

// Enquanto a sessão estiver ativa, chamar sequencialmente ao receber novos pontos
// ou em intervalos definidos pelo app (e repetir quando a rede voltar):
try await healthKit.syncCurrent(using: uploader)

// Ação de usuário: finalizar treino.
let result = try await healthKit.finish()
try await healthKit.upload(result, using: uploader)

// Somente se o aluno ativou a gravação no Apple Saúde:
try await healthKit.saveToAppleHealth(result, activityType: .traditionalStrengthTraining)
```

O app precisa persistir `result` localmente antes de tentar o upload e repetir em caso de falha de rede, preservando o mesmo `workoutID`. Também deve reconciliar amostras que cheguem ao Apple Saúde **depois** do encerramento; o módulo faz apenas uma leitura final no momento de finalizar. A UI não deve prometer FC em tempo real: o HealthKit entrega novos registros quando cada fonte os grava/sincroniza.

## Contrato esperado da API

`PUT /workouts/{workoutID}/heart-rate` com autenticação do aluno e chave de idempotência. O caminho exato é configurado pelo app ao criar o `HTTPWorkoutHeartRateUploader`. O corpo JSON contém `workoutID`, `startedAt`, `endedAt` (nulo durante a execução), `averageBeatsPerMinute`, `maximumBeatsPerMinute` e `points[]` (`healthKitID`, `timestamp`, `beatsPerMinute`, `sourceName`, `deviceName`). Datas usam ISO 8601 UTC. O servidor deve:

- validar que a sessão pertence ao aluno autenticado e que a autorização de compartilhamento está ativa;
- aceitar apenas amostras dentro do intervalo do treino e valores plausíveis para armazenamento, sem usá-los para diagnóstico;
- deduplicar pontos por `(aluno, healthKitID)` e fazer upsert da sessão por `workoutID`;
- recalcular média e máximo no servidor a partir dos pontos aceitos e guardar origem, horário e histórico de atualização;
- permitir repetição segura do `PUT` e exclusão/revogação conforme política de dados.

**Média:** no módulo é a média aritmética das amostras recebidas. Não é média contínua ponderada pelo tempo. Se o produto exigir média fisiológica da sessão, é necessário definir e implementar a regra de agregação no backend.

## Validação pendente

- Compilar com o Xcode no target real e revisar disponibilidade das APIs no deployment target escolhido.
- Testar no iPhone: autorização de leitura negada/parcial, escrita negada, amostras de diferentes origens no Apple Saúde, ausência de amostras, amostras atrasadas, exclusão na fonte e rede indisponível.
- Integrar a API real, persistência local de upload e verificação de acesso do aluno no backend.

Referências: [autorização HealthKit](https://developer.apple.com/documentation/HealthKit/authorizing-access-to-health-data), [consulta ancorada](https://developer.apple.com/documentation/healthkit/hkanchoredobjectquery), [HKWorkoutBuilder](https://developer.apple.com/documentation/healthkit/hkworkoutbuilder).
