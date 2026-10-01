# Requisitos do app de treino e acompanhamento de saúde

**Versão:** 1.0 — 30/09/2026  
**Objetivo:** especificar um produto próprio para personal trainers, alunos, nutricionistas e profissionais habilitados em terapias integrativas, com treino como fluxo central.  
**Estado:** proposta de requisitos para validação de negócio, jurídica e técnica; não é especificação de interface final.

## 1. Base e limites das referências

As quatro capturas fornecidas mostram a **área do aluno** da MFIT Personal: (1) início com profissional, registro CREF, frequência semanal e atalhos para treinos, extras, avaliações, progresso, faturas e arquivos; (2) lista de rotinas com período, objetivo e nível, além da aba aeróbico; (3) treinos da rotina com histórico de execuções, evolução, feedback e acesso ao treino; (4) visualização do treino com início de sessão, exercícios, vídeo, séries, carga e intervalo. As imagens servem como referência de fluxo e hierarquia. Marca, logo, fotos, textos e mídia demonstrativa de terceiros exigem identidade e acervo próprios ou licenciamento.

O site público da MFIT confirma cadastro e acompanhamento de alunos, anamnese, avaliações, treinos extras, biblioteca de vídeos, clonagem e periodização de rotinas, feedback, gráficos de evolução, documentos, pagamentos e uso do aluno em qualquer horário. São capacidades observadas na referência, **não** funcionalidades do novo produto já implementadas. Fontes: [página inicial](https://www.mfitpersonal.com.br/), [gestão de alunos](https://www.mfitpersonal.com.br/pages/gerenciamento-de-alunos.html), [prescrição de treinos](https://www.mfitpersonal.com.br/pages/prescricao-de-treinos.html), [soluções para alunos](https://www.mfitpersonal.com.br/pages/solucoes-para-alunos.html), [gestão financeira](https://www.mfitpersonal.com.br/pages/gestao-financeira.html).

## 2. Visão do produto

**Proposta:** um app no qual o aluno recebe treino prescrito, o executa e acompanha a evolução; pode também compartilhar, sob permissão específica, registros nutricionais, sessões de terapias e dados de frequência cardíaca obtidos pelo HealthKit com os profissionais correspondentes. O profissional acompanha seus atendidos em um painel próprio, com histórico e ações claras.

**Perfis:** aluno; personal trainer; nutricionista; profissional de terapia integrativa/acupuntura; administrador da organização; suporte operacional com acesso restrito. Um usuário pode ter mais de um vínculo profissional, mas cada ação registra o perfil usado. Atendimentos presenciais e remotos são suportados.

**Princípio de produto:** treino é o núcleo. Nutrição e terapias são módulos independentes, habilitados por organização/plano, com compartilhamento mínimo e explícito entre equipes. Dados do HealthKit apoiam o acompanhamento de bem-estar; não geram diagnóstico nem substituem avaliação profissional.

## 3. Jornadas essenciais

1. **Entrada do aluno:** recebe convite; cria acesso próprio; aceita termos e avisos pertinentes; preenche objetivos, disponibilidade, restrições e questionário inicial; escolhe quais profissionais podem ver cada grupo de dados.
2. **Prescrição:** personal analisa anamnese e avaliações, cria rotina por período, inclui sessões de força/aeróbico e publica uma versão ao aluno.
3. **Execução:** aluno abre a rotina, vê exercícios e mídia, inicia a sessão, registra séries/cargas/repetições/tempo, usa cronômetro, pausa/retoma e finaliza com esforço percebido e feedback.
4. **Acompanhamento:** aluno e personal veem frequência real, adesão, evolução de carga e avaliações; o personal ajusta e republica a prescrição com histórico.
5. **Nutrição:** nutricionista recebe acesso consentido, faz avaliação, prescreve plano alimentar e acompanha adesão e evolução; aluno registra refeições, água, dificuldades e feedback.
6. **Terapias:** profissional habilitado registra avaliação, agenda sessão, documenta técnica, orientações e evolução; aluno visualiza agenda, orientações e pode relatar como se sente após a sessão.
7. **HealthKit:** aluno autoriza no iPhone a leitura de frequência cardíaca do Apple Saúde, acompanha a sincronização e pode revogar o acesso; profissionais veem somente indicadores compartilhados com autorização.

## 4. Requisitos funcionais

### 4.1 Identidade, vínculo e acesso

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-01 | P0 | Cadastro por convite ou autoinscrição; autenticação com recuperação de conta e verificação de e-mail/telefone. Um aluno não acessa dados de outro. |
| RF-02 | P0 | Vínculo aluno-profissional com estado `pendente/ativo/encerrado`; retirada do vínculo encerra novos acessos e preserva registros pelo prazo aplicável. |
| RF-03 | P0 | Perfis e permissões por papel e por finalidade. Personal edita treino; nutricionista edita plano alimentar; terapeuta edita seu atendimento; aluno vê seus dados; administrador não ganha acesso clínico automaticamente. |
| RF-04 | P0 | Cadastro do profissional com nome público, foto, área de atuação, conselho/registro quando aplicável, comprovantes e estado de validação antes de publicar prescrições ou atender pelo app. |
| RF-05 | P1 | Organização/equipe com múltiplos profissionais e alunos; convite, substituição de responsável e trilha de transferência. |

### 4.2 Início e comunicação

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-06 | P0 | Início do aluno com saudação, profissional responsável, próxima sessão, frequência dos 7 dias da semana e atalhos para Treinos, Avaliações, Progresso, Nutrição, Terapias, Apple Saúde e Arquivos. Dia sem treino planejado aparece como descanso, diferente de falta. |
| RF-07 | P0 | Central de notificações para rotina publicada/alterada, sessão agendada, resposta a feedback e lembrete configurável. Cada item abre o conteúdo correto e pode ser marcado como lido. |
| RF-08 | P1 | Mensagens entre aluno e profissional dentro do app com anexos autorizados; atalho de WhatsApp apenas com ação explícita do usuário e sem colocar dado sensível na mensagem automática. |
| RF-09 | P1 | Agenda para sessões presenciais/remotas, confirmação, cancelamento, reagendamento e fuso horário; registrar política de cancelamento do prestador. |

### 4.3 Treinos — prescrição e biblioteca

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-10 | P0 | Personal cria uma **rotina** com nome, objetivo, nível, início/fim, frequência sugerida, observações e estado `rascunho/publicada/encerrada`. Pode copiar uma rotina e adaptar a cópia sem alterar o histórico original. |
| RF-11 | P0 | Cada rotina contém **sessões** ordenadas (ex.: Treino 1, Peito e ombro), inclusive aeróbicas; cada sessão tem exercícios em ordem, séries com meta de repetições ou duração, carga sugerida, intervalo, instruções e alternativa/substituição. |
| RF-12 | P0 | Biblioteca de exercícios pesquisável por nome, grupo muscular, equipamento e finalidade; cada item inclui descrição, cuidados, mídia própria/licenciada e versão. Profissional pode cadastrar exercício e vídeo próprios. |
| RF-13 | P0 | Publicação gera versão imutável para execuções já iniciadas. Alterações posteriores ficam visíveis com data e descrição; aluno vê qual versão executará. |
| RF-14 | P1 | Treino extra avulso com prazo de validade e motivo, sem interferir automaticamente na periodização principal. |
| RF-15 | P1 | Planejamento por semanas/blocos, deload e progressão; alertas de término de rotina e necessidade de revisão, sem reajuste automático de carga sem validação do personal. |
| RF-16 | P1 | Exportação/visualização offline de treino publicado com mídia selecionada e aviso de versão/validade; sincronização posterior sem perder registros. |

### 4.4 Treinos — execução e evolução

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-17 | P0 | Tela de rotina lista sessões, número de execuções, última execução, acesso à evolução, feedbacks e botão Ver treino. |
| RF-18 | P0 | Antes de iniciar, sessão está em visualização; ao iniciar, registra horário e permite marcar série realizada, repetição, carga, duração, distância, dificuldade/observação e intervalo com cronômetro. Pausa, retomada e abandono são estados distintos. |
| RF-19 | P0 | Finalização solicita feedback simples (esforço percebido, dor/desconforto opcional e comentário) e gera registro de execução. A frequência conta sessões **concluídas**, nunca simples abertura da tela. |
| RF-20 | P0 | Evolução por exercício e por período: cargas/repetições/volume e melhor marca, com comparações apenas de medidas equivalentes; mostrar origem e data de cada registro. |
| RF-21 | P0 | Personal vê adesão planejada versus realizada, feedback pendente, sessões concluídas e sinais relatados que exigem contato; pode responder e ajustar prescrição. |
| RF-22 | P1 | Aeróbico: prescrever modalidade, duração/distância, zona ou intensidade percebida, aquecimento e volta à calma; execução pode associar dados de frequência cardíaca do HealthKit quando disponíveis. |

### 4.5 Avaliações e progresso

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-23 | P0 | Anamnese editável pelo aluno e revisável pelo profissional, com objetivo, histórico de exercício, limitações, lesões relatadas, disponibilidade, equipamentos e aceite de uso. Alteração relevante sinaliza revisão da prescrição. |
| RF-24 | P0 | Avaliação física com data, avaliador, método/unidade, medidas selecionadas, fotos opcionais e observações; não comparar métodos diferentes como se fossem a mesma medida. |
| RF-25 | P0 | Meu Progresso reúne frequência, avaliações, evolução de exercícios e metas definidas com profissional; gráficos mostram período e dados ausentes sem estimativas silenciosas. |
| RF-26 | P1 | Metas de processo (treinos/semana, hidratação, sono informado) e resultado (medidas), com revisão manual e histórico. |
| RF-27 | P1 | Arquivos do aluno: laudos, exames e documentos enviados por ele ou por profissional autorizado, com tipo, data, origem, permissões e opção de exclusão conforme política. |

### 4.6 Nutrição

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-28 | P0 | Nutricionista validado cadastra avaliação nutricional, objetivos, restrições/alergias relatadas, preferências, horários, medidas e acompanhamento; dados alimentares não são abertos automaticamente ao personal. |
| RF-29 | P0 | Plano alimentar versionado, com período, refeições/horários, alimentos e porções, substituições, observações e autoria; publicação/alteração gera aviso e mantém planos anteriores auditáveis. **Prescrição dietética é ação do nutricionista habilitado.** |
| RF-30 | P0 | Diário alimentar do aluno com refeição, horário, texto/foto opcional, água e adesão ao plano; permite registrar item não planejado sem punição. Profissional comenta e ajusta o plano. |
| RF-31 | P1 | Metas nutricionais individualizadas e acompanhamento por período; cálculos nutricionais mostram tabela/fonte, unidade e grau de aproximação. Não gerar prescrição automática de ingestão a partir de métricas de atividade. |
| RF-32 | P1 | Alertas de alergias/restrições no editor e no plano visível ao aluno; indicação de suplementos exige revisão do nutricionista e observação de condições clínicas quando aplicável. |

### 4.7 Terapias integrativas, incluindo acupuntura

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-33 | P0 | Catálogo de serviços por modalidade e profissional habilitado; habilitação e escopo são verificados por modalidade antes de abrir agenda ou prontuário. |
| RF-34 | P0 | Ficha de atendimento de acupuntura com queixa/objetivo informado, avaliação profissional, contraindicações/precauções, técnica utilizada, pontos/regiões quando pertinentes, duração, intercorrências e orientações pós-sessão. |
| RF-35 | P0 | Consentimento específico para procedimento, com versão, data, profissional, riscos informados e registro de recusa/retirada; sessão não pode ser marcada como realizada sem documentação necessária. |
| RF-36 | P0 | Agenda e histórico de sessões, evolução relatada pelo paciente, revisão do plano terapêutico pelo profissional e encaminhamento para assistência médica quando houver sinal de alerta. |
| RF-37 | P1 | Outras práticas (ex.: meditação guiada, massoterapia) entram por configuração própria de escopo, formação, registro e documentação, após análise jurídica e clínica de cada modalidade. |

### 4.8 Apple Saúde/HealthKit e frequência cardíaca

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-38 | P0 | Tela **Apple Saúde** explica a leitura de frequência cardíaca, finalidade, profissional destinatário e período. Conectar, pausar e revogar são ações separadas; o treino permanece utilizável sem acesso ao HealthKit. |
| RF-39 | P0 | No iPhone, integrar exclusivamente com **Apple Saúde/HealthKit**. Solicitar leitura de frequência cardíaca somente após ação explícita do aluno. A disponibilidade depende de amostras existentes no Apple Saúde e da autorização concedida. |
| RF-40 | P0 | Importar frequência cardíaca quando disponível e autorizada. Mostrar bpm, horário, origem, última sincronização e ausência de dados, sem introduzir outras métricas no escopo desta integração. |
| RF-41 | P0 | Associar amostras de batimentos à sessão pelo intervalo de tempo e origem, com FC média/máxima e gráfico de evolução; enviar amostras à API durante o treino e consolidar ao finalizar. Não inferir medição contínua quando houver amostras esparsas. Registrar lacunas e origem divergente. |
| RF-42 | P0 | Deduplicar dados importados por identificador de origem, tipo e intervalo; preservar proveniência e histórico de correção/exclusão da fonte. Reprocessamento de sincronização não deve duplicar sessão nem indicadores. |
| RF-43 | P0 | Compartilhamento de indicadores com personal/nutricionista/terapeuta é granular; painel do profissional diferencia dados informados, medidos e estimados. Revogação bloqueia novas leituras e novos compartilhamentos, com tratamento de dados já recebidos conforme base legal e retenção definida. |
| RF-44 | P1 | Configuração de zonas de treino pelo personal, com método documentado e revisão individual. Alertas ao aluno são de orientação de exercício e não diagnóstico ou emergência; valores inesperados devem sugerir pausa e avaliação profissional conforme protocolo. |

#### 4.8.1 Integração específica com Apple HealthKit

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| HK-01 | P0 | O app iOS solicita **autorização explícita no iPhone** para ler frequência cardíaca do Apple Saúde antes da primeira consulta. Solicitar somente o tipo necessário; se o usuário negar, o treino continua sem FC e o app não afirma que a permissão foi concedida. |
| HK-02 | P0 | Ler amostras de FC disponíveis no HealthKit sem filtrar a origem. Qualquer amostra gravada no Apple Saúde pode entrar se o usuário tiver autorizado a leitura. |
| HK-03 | P0 | Ao iniciar o treino, abrir consulta das amostras de FC e associar à sessão ativa as amostras cujo horário pertence ao intervalo da sessão. Guardar identificador da amostra, horário, valor em bpm e origem informada pelo HealthKit. |
| HK-04 | P0 | Enquanto o treino estiver em execução, atualizar a linha do tempo e enviar à API os pontos recebidos com o identificador estável da sessão. A interface mostra o horário da última amostra e da última sincronização; não promete atualização contínua quando a fonte entregar dados com atraso. |
| HK-05 | P0 | Ao finalizar, fazer leitura final do intervalo, deduplicar amostras, enviar o fechamento à API e armazenar pontos e resumo: FC média, FC máxima, período e método de cálculo. A API valida a titularidade da sessão, aceita repetição idempotente e recalcula o resumo dos pontos aceitos. |
| HK-06 | P0 | Registrar o treino concluído no Apple Saúde **somente** se o aluno habilitar essa opção e conceder autorização de escrita para treinos no iPhone. A permissão de escrita é distinta da de leitura; não regravar amostras de FC importadas. Guardar identificador externo da sessão para evitar duplicação. |
| HK-07 | P0 | Falha de rede não descarta amostras ou treino: persistir envio pendente no aparelho e repetir com o mesmo identificador quando a conexão voltar. Envio durante a sessão e fechamento devem preservar ordem lógica e não sobrescrever um fechamento com estado parcial. |
| HK-08 | P1 | Reconciliar amostras que apareçam no Apple Saúde após a conclusão, por sincronização tardia da fonte, respeitando período autorizado, origem e exclusões posteriores. Exibir a data da última revisão do resumo. |

**Critério de produto:** o HealthKit fornece amostras gravadas no Apple Saúde; a chegada delas depende da fonte. “Durante o treino” significa acompanhar o que estiver disponível naquele momento, sem promessa de atualização contínua.

### 4.9 Planos, cobrança e operação

| ID | Prioridade | Requisito e aceite mínimo |
|---|---|---|
| RF-47 | P1 | Catálogo de serviços e planos por profissional/organização, com preço, duração, escopo, vigência, renovação e termos. |
| RF-48 | P1 | Faturas com situação, vencimento, itens e comprovante; pagamento por PSP terceirizado com Pix/cartão e conciliação por webhook idempotente. Não armazenar dados completos de cartão. |
| RF-49 | P1 | Relatórios operacionais de alunos ativos, adesão, atendimentos e receitas, limitados ao escopo do gestor e sem exposição de dados sensíveis desnecessários. |
| RF-50 | P0 | Painel administrativo para validar profissionais, gerenciar catálogos e permissões, atender solicitações LGPD, auditar acessos e configurar retenção. |

## 5. Regras de negócio e aceitação transversal

- **Frequência:** sessão concluída é contada na data local do aluno; dois treinos no mesmo dia podem ser registrados, mas o cartão semanal distingue dias ativos de total de sessões. Falta requer treino planejado para aquela data e prazo transcorrido; descanso não é falta.
- **Histórico:** prescrições, planos e avaliações têm autor, data e versão. Uma execução sempre aponta para a versão usada. Correções não apagam registros anteriores; registram motivo e responsável.
- **Desconforto:** feedback de dor, tontura ou mal-estar gera aviso para o profissional responsável e orientação de buscar atendimento adequado quando necessário; não produz diagnóstico automático.
- **Autonomia do aluno:** aluno pode registrar treino realizado sem conectar o Apple Saúde, pode ocultar dados não necessários e pode solicitar correção/exportação de dados conforme política.
- **Separação profissional:** um personal não prescreve dieta pelo módulo nutricional; um terapeuta não altera treino; acesso cruzado exige permissão e finalidade explícitas.
- **Confiabilidade das amostras:** indicadores ausentes, atrasados ou de baixa qualidade aparecem como tal; não preencher lacunas com valores inventados nem usar o dado para avaliação clínica automatizada.

## 6. Matriz de acesso resumida

| Dado/ação | Aluno | Personal | Nutricionista | Terapeuta | Administrador |
|---|---|---|---|---|---|
| Próprios dados e permissões | Ver/gerir | — | — | — | Suporte restrito |
| Prescrever e ajustar treino | Ver/executar | Criar para vinculados | — | — | — |
| Avaliação física | Ver | Criar para vinculados | Ver se autorizado | Ver se autorizado | — |
| Plano e diário alimentar | Ver/registrar | Resumo se autorizado | Prescrever/acompanhar vinculados | — | — |
| Ficha e sessão terapêutica | Ver | — | — | Criar para vinculados | — |
| Frequência cardíaca via HealthKit | Conectar/autorizar/revogar | Ver dados autorizados | Ver dados autorizados | Ver dados autorizados | Operar sem conteúdo por padrão |

## 7. Requisitos não funcionais e de conformidade

| ID | Requisito verificável |
|---|---|
| RNF-01 | Dados de saúde são sensíveis. Definir, por finalidade, base legal, controlador/operador, retenção, compartilhamento e descarte; registrar consentimentos quando forem a base escolhida e permitir sua revogação. Acesso ao dado usa menor privilégio. |
| RNF-02 | Criptografia em trânsito e em repouso, segregação por organização, MFA para profissionais e administradores, sessão revogável, backups testados e logs de acesso a dados sensíveis sem registrar conteúdo clínico nos logs. |
| RNF-03 | Trilhas auditáveis para leitura, alteração, exportação, concessão/revogação de acesso e publicação de prescrição; data/hora e identidade do ator. |
| RNF-04 | Exportação dos dados próprios em formato legível e estruturado; fluxo para solicitação de acesso, correção e exclusão sujeito a obrigações de retenção. Política de privacidade clara antes de conectar saúde. |
| RNF-05 | Disponibilidade alvo inicial de 99,5% mensal para API; monitoramento de erro, fila de sincronização e restauração de backup testada. Metas finais de RPO/RTO devem ser fixadas antes do lançamento. |
| RNF-06 | Tempo alvo: tela inicial e lista de treinos em até 3 s no p95 em rede móvel adequada; ação de marcar série responde localmente em até 300 ms e sincroniza de forma resiliente. |
| RNF-07 | App móvel iOS/Android em pt-BR, layouts para fontes ampliadas e leitores de tela; contraste, rótulos, alvos de toque e navegação por teclado conforme WCAG 2.2 AA nas interfaces aplicáveis. |
| RNF-08 | Vídeos com legenda/texto alternativo instrucional; compressão e cache controlado; foto e mídia de terceiros só com direitos de uso. |
| RNF-09 | Sincronização offline tolera repetição e conflito; cada gravação tem identificador idempotente, horário original e estado de envio. |
| RNF-10 | Antes de qualquer promessa de detecção de doença, risco clínico, urgência ou recomendação terapêutica automática, submeter a finalidade pretendida à avaliação regulatória de software como dispositivo médico. |

## 8. Integrações e dependências

| Integração | Caminho proposto | Dependência/limite |
|---|---|---|
| Apple Saúde | HealthKit no iPhone | Permissão granular para leitura de frequência cardíaca e, separadamente, escrita de treino concluído; amostras dependem do que cada fonte grava no Saúde. |
| Pagamentos | PSP com Pix e cartão | Escolha de provedor, tarifas e conciliação ficam para discovery comercial. |
| Mensageria | Push/e-mail e WhatsApp por abertura voluntária | Mensagens sensíveis ficam no app; custos e regras do provedor devem ser verificados. |

Documentação da integração: [autorização HealthKit](https://developer.apple.com/documentation/HealthKit/authorizing-access-to-health-data), [consulta de amostras](https://developer.apple.com/documentation/healthkit/hkanchoredobjectquery), [registro do treino](https://developer.apple.com/documentation/healthkit/hkworkoutbuilder).

## 9. Segurança assistencial e responsabilidades

- Nutrição: restringir prescrição dietética ao profissional habilitado, conforme [Lei 8.234/1991](https://www.planalto.gov.br/ccivil_03/leis/1989_1994/l8234.htm). O personal pode ver apenas o que o aluno e o nutricionista permitirem para coordenação do treino.
- Acupuntura: aplicar verificação de habilitação e escopo conforme [Lei 15.345/2026](https://www.planalto.gov.br/ccivil_03/_ato2023-2026/2026/lei/l15345.htm); revisar regras do conselho correspondente e protocolos antes de habilitar cada profissional.
- Privacidade: a [ANPD](https://www.gov.br/anpd/pt-br/acesso-a-informacao/perguntas-frequentes) classifica dados referentes à saúde como sensíveis; a base legal e a finalidade devem ser definidas por tratamento, sem presumir que todo uso será coberto por um único consentimento.
- Finalidade do software: a [Anvisa](https://www.gov.br/anvisa/pt-br/centraisdeconteudo/publicacoes/produtos-para-a-saude/manuais/software-como-dispositivo-medico-perguntas-e-respostas) trata software médico sob regras específicas. O escopo inicial é acompanhamento de exercício e bem-estar; qualquer função diagnóstica ou decisão clínica automatizada requer análise própria antes de desenvolvimento e divulgação.

## 10. Entrega por fases

| Fase | Escopo e condição de saída |
|---|---|
| **MVP — P0** | Acesso e vínculos; painel do aluno; biblioteca e prescrição versionada; execução e frequência corretas; avaliações básicas; feedback/evolução; nutrição com plano e diário; terapia com agenda/ficha/consentimento; HealthKit em leitura, envio de FC à API e gravação opcional do treino no Apple Saúde; permissões granulares, auditoria e exportação. Conclui com jornada completa demonstrável por cada perfil e testes de isolamento de dados. |
| **Fase 2 — P1** | Mensagens, agenda ampla, treinos extras, periodização, metas, offline, cobranças, relatórios, zonas de treino e expansão de métricas. Conclui com conciliação financeira e sincronização resiliente testadas. |
| **Fase 3 — P2** | Análises avançadas dos dados do HealthKit somente após validação de qualidade e análise regulatória. |

## 11. Casos de teste de aceitação prioritários

1. Aluno vinculado a dois profissionais recebe dois planos de treino sem misturar dados; cada profissional vê apenas seus vinculados e métricas autorizadas.
2. Personal publica versão B enquanto aluno executa versão A; execução permanece ligada à A e próxima abertura apresenta B.
3. Abrir vídeo e sair não aumenta frequência; concluir sessão aumenta frequência na data local correta; dia de descanso não vira falta.
4. Sincronização repetida de FC importa uma amostra uma vez; dado excluído na fonte é tratado segundo política de sincronização; gráfico indica lacuna e última sincronização.
5. Aluno nega ou revoga permissão de batimentos: treino continua utilizável e nenhum dado novo é exibido ao profissional.
6. Nutricionista altera plano: aluno vê nova versão e histórico; personal sem autorização não vê diário alimentar.
7. Profissional sem habilitação validada não publica prescrição dietética nem registra procedimento de acupuntura.
8. Sessão de acupuntura exige registro de consentimento e campos obrigatórios antes de ser concluída.
9. Usuário solicita exportação: pacote contém seus registros e proveniência, sem dados de terceiros; auditoria registra solicitação e entrega.
10. No iPhone, negar a leitura de FC mantém o treino funcional; autorizar leitura permite receber amostras de diferentes fontes presentes no Apple Saúde, sem filtro por marca.
11. FC chega durante o treino: a tela e a API associam cada amostra à sessão correta; reenvio não duplica pontos; média, máximo e evolução refletem os pontos aceitos.
12. Aluno conclui o treino e nega escrita no Apple Saúde: fechamento na API ocorre normalmente e nenhum treino é criado no Saúde. Ao autorizar escrita, cria-se um único treino com o período correto.
13. Novas amostras aparecem no Apple Saúde após o treino ou a rede cai durante o envio: dados pendentes são reconciliados sem duplicar sessão nem regredir o estado concluído.

## 12. Decisões de negócio a fechar antes da implementação

1. Público inicial: profissionais autônomos, clínicas/equipes ou ambos; define faturamento, multiusuário e isolamento.
2. Modelo comercial: cobrança do profissional, do aluno, por módulo ou por organização; quem emite fatura ao aluno.
3. Profundidade do MVP de nutrição: diário simples e plano prescrito (proposto) versus cálculo nutricional completo com base de alimentos.
4. Modalidades terapêuticas de lançamento além de acupuntura e processo de validação de habilitação.
5. Política de retenção de prontuários/documentos, contratos entre organização e profissionais e análise jurídica de bases legais.
6. Frequência de atualização esperada para os dados de FC durante o treino e comportamento da interface quando as amostras chegarem com atraso.

