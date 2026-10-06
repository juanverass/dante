# Organização de Dante.Application por feature (#203, AD-49)

A Application é organizada verticalmente por feature (Epic #202): uma pasta por
feature, com namespace `Dante.Application.<Feature>`. DTOs, ports e AppServices
ficam na feature que os usa; não existe projeto `Dante.Application.DTO`, e a
Application continua sem EF Core, Npgsql ou outro provider técnico.

## Regras gerais

- **um tipo público de topo por arquivo**, com o nome do tipo (`AcessoAoBrain.cs`,
  `IIndiceDeBusca.cs`). Não criar arquivos agregadores como `ContratosDe*.cs` nem
  declarar DTOs/ports dentro do arquivo do AppService;
- **o namespace é sempre o da feature**, inclusive dentro de subpastas. Subpasta
  organiza arquivos, não muda o contrato público nem exige `using` novo nos
  consumidores. `HexagonalArchitectureTests` recusa namespaces abaixo da feature;
- tipo usado por várias features fica na feature dona do conceito (`ProvenienciaDto`
  em `Conhecimentos`, `AcessoAoBrain` em `SegurancaDoBrain`). `Comum` guarda só a base
  genérica de aplicação (CRUD, `IRepository`, `IUnitOfWork`);
- nomes em PT-BR com os sufixos técnicos estabelecidos: `AppService`, `Repository`,
  `Dto`, `SearchDto` (AD-36, [base CRUD](crud.md)).

## Módulo simples e módulo complexo

Um módulo é **complexo** quando tem ports além do próprio repository (índices,
consultas, geradores, estado externo) ou casos de uso internos. Os demais são
**simples**. A diferença é só física:

| Elemento | Módulo simples | Módulo complexo |
| --- | --- | --- |
| AppService/fachada (`<Conceito>AppService`) e sua interface pública | raiz | raiz |
| Serviço público sem estado da feature (`ResolvedorDeIntencaoDoBrain`) | raiz | raiz |
| DTO, SearchDto e enum do contrato público | raiz | `Contratos/` |
| Repository port (`I<Entidade>Repository`) | raiz | `Portas/` |
| Outras ports implementadas pela Infrastructure e records trocados só com elas | — | `Portas/` |
| Mapping da feature | raiz | raiz |
| Validação de entrada | raiz | `Validacao/` |
| Caso de uso auxiliar interno | raiz | `CasosDeUso/` |

Subpastas só existem quando há arquivo para elas. Cada feature com mappings tem um
`<Entidade>Mapping` `internal static` na raiz, composto por
`Mapeamento/MapeamentosDaApplication` ([mappings](mapping.md), #204). Validators
seguem a [validação de entrada](#validação-de-entrada-e-regra-de-negócio-205-ad-52);
regra de negócio continua no Domain ou no AppService.

Módulo simples (`Projetos`):

```text
Projetos/
├─ IProjetoAppService.cs
├─ IProjetoRepository.cs
├─ ProjetoAppService.cs
├─ ProjetoMapping.cs
├─ ProjetoDto.cs
└─ ProjetoSearchDto.cs
```

Módulo complexo (`BuscaDoBrain`), todos os tipos em `Dante.Application.BuscaDoBrain`:

```text
BuscaDoBrain/
├─ BuscaDoBrainAppService.cs
├─ Contratos/
│  ├─ BuscaDoBrainDto.cs
│  ├─ BuscaDoBrainSearchDto.cs
│  ├─ OrigemDoResultado.cs
│  └─ ResultadoDaBuscaDto.cs
└─ Portas/
   ├─ IGeradorDeEmbedding.cs
   ├─ IIndiceDeBusca.cs
   ├─ MatchDaBusca.cs
   └─ ModeloEmbedding.cs
```

## Classificação atual

| Complexos | Simples |
| --- | --- |
| `AuditoriaDoBrain`, `BuscaDoBrain`, `ConversaDoBrain`, `DocumentosFonte`, `QualidadeDoBrain`, `ConstrucaoDeContexto`, `MetricasDoBrain` | `CapturaDeConhecimento`, `Conhecimentos`, `ContextosDeTrabalho`, `EspacosDeConhecimento`, `Projetos`, `RelacoesDeConhecimento`, `SegurancaDoBrain` |

Quando um módulo simples ganhar uma port própria ou um caso de uso interno, ele passa
a complexo e seus contratos/ports vão para as subpastas no mesmo PR.

`Agentes`, `Anexos`, `Contextos` e `Uso` são legado extraído do Worker e mantêm a
organização existente até migração explícita (AD-36, AD-38). `Mapeamento` e `Comum`
são infraestrutura da própria Application.

## Validação de entrada e regra de negócio (#205, AD-52)

| Tipo de verificação | Onde fica | Exemplos |
| --- | --- | --- |
| **Validação de entrada**: só olha os valores recebidos | `<Conceito>Validator` da feature (raiz no módulo simples, `Validacao/` no complexo) ou o helper `Comum/ValidacaoDeEntrada` | `Guid.Empty` obrigatório, limite entre 1 e N, texto obrigatório/tamanho máximo, enum definido, formato aceito, combinação estrutural de campos |
| **Regra contextual**: depende de repository, estado persistido, identidade ou policy | AppService/caso de uso | espaço arquivado é somente leitura, projeto de outro espaço, usuário sem acesso, decisão confirmada no mesmo escopo |
| **Invariante**: vale para toda instância da entidade | Domain | revisão esperada desatualizada, consolidação não reduz sensibilidade, resolução de conflito exige decisão explícita |

Validators são classes `internal static`, sem estado nem dependências: recebem o
DTO/SearchDto ou os parâmetros e lançam `ArgumentException`/`ArgumentOutOfRangeException`
com mensagem e `ParamName` estáveis. Não acessam repository, unit of work, DbContext ou
Npgsql. O AppService chama o validator no mesmo ponto em que o check ficava, de modo
que a ordem entre validação de acesso e validação de entrada não muda. Normalização
(por exemplo `Trim` de filtros) continua no AppService.

`ValidacaoDeEntrada` concentra os checks repetidos entre features: `ExigirFaixa`
(limites, deslocamentos e profundidades), `ExigirId` e `ExigirEscopo` (espaço
obrigatório, projeto opcional mas nunca `Guid.Empty`). Check exclusivo de uma feature
fica no validator dela. Proteção de segredos (`ProtecaoDeSegredos`) é policy de
segurança e não é validator. `ValidacaoDeEntradaTests` testa os validators sem
repository nem banco. Não há FluentValidation: os checks atuais são poucos e diretos.

## Conversa como fachada (#206)

ConversaDoBrainAppService mantém autorização inicial, resolução determinística,
seleção de alvo e ciclo da proposta/confirmar/cancelar. Os casos ConsultaDaConversa,
CapturaDaConversa, FontesDaConversa, AlteracoesDaConversa, InspecaoDaConversa,
ContextoDeTrabalhoDaConversa e AvaliacaoDaConversa recebem suas dependências por
construtor e o ContextoDaConversa da chamada. Eles ficam em CasosDeUso com namespace
da feature; os tipos são públicos para composição da DI, mas seus métodos de execução
são internos à Application e aos testes. Adapters entram pela fachada autorizada.

ContextoDaConversa compartilha estado, acesso e mensagem daquela chamada; não resolve
serviços. A confirmação continua consumida atomicamente no store antes de executar
a alteração, e a alteração revalida revisão, escopo e sensibilidade. Os casos podem
ser testados diretamente com ports simulados sem Worker, Telegram ou banco.

## Políticas puras (#207)

AnaliseDeQualidade recebe itens/arestas já autorizados e instante explícito, sem
consulta ou mutação. ElegibilidadeDeContexto filtra valores do pedido;
SelecaoDeContexto recebe candidatos protegidos e preserva precedência, deduplicação,
sobreposição Unicode, orçamento e rastreabilidade. AgregacaoDeMetricas calcula
retomadas/qualidade com dados ausentes preservados e ApresentacaoDeMetricas formata
o resultado. Os AppServices continuam responsáveis por autorização, leitura dos
ports e transações. Os métodos estáticos de métricas/tokens existentes delegam às
políticas para manter os consumidores compatíveis.

## Composição dos serviços (#208, AD-54)

`AddApplication()` registra os serviços próprios do núcleo: mapping, policy,
autorização, AppServices e casos de uso da conversa, por lista explícita em
`Composicao/ServicosDoBrain.cs` com namespace `Dante.Application`. Infrastructure
registra ports com adapters técnicos, DbContext, repositories e administração. Os
hosts continuam usando `AddApplication().AddInfrastructure(configuration)`.

Mapping/policy são singleton; autorização/AppServices/casos da conversa são scoped.
Validators e políticas puras estáticas não precisam de DI. ContextoDaConversa
pertence à chamada, não ao container. TryAdd permite repetir AddApplication e
preserva serviços/fakes que já foram registrados.

O Brain é opcional. Os serviços que exigem ports usam factories scoped com
ActivatorUtilities: o host sem banco inicia/valida normalmente, mas resolver um caso
de uso sem seus ports falha explicitamente. `ValidateOnBuild` não percorre factories;
por isso `ApplicationCompositionTests` também resolve todos os serviços da lista
com adapters configurados e verifica sua identidade por escopo. O DbContext da DI
exige autorização e nunca assume modo administrativo na ausência de AddApplication.

Testes de Application podem compor AddApplication com repositories/UoW fakes e
resolver o AppService pela interface, sem referenciar Infrastructure ou configurar
banco. Ao adicionar um serviço com dependências, inclua-o na lista da composição e
mantenha o teste de resolução integral.

## Responsabilidade de cada camada (#209)

| Camada | Contém | Não contém |
| --- | --- | --- |
| Domain | entidades, invariantes, enums e value objects do modelo | interfaces/ports, DTO, mapper, validator de entrada, repository, provider técnico ou referência fora do runtime |
| Application | AppServices, casos de uso, DTO/SearchDto, mappings, validators, policies e ports (`I<Entidade>Repository`, índices, consultas, estado externo) | EF Core, Npgsql, Telegram, ASP.NET, IO de arquivo/processo/rede |
| Infrastructure | adapters que implementam as ports, DbContext, `<Entidade>DbMapping`, `<Entidade>Repository`, migrations e administração | AppService, regra de caso de uso, registro de serviço da Application |
| Worker/WebApi | entrada/saída do host (Telegram, HTTP, sessões) e composição `AddApplication().AddInfrastructure(...)` | implementação de port da Application, AppService, validator, mapping, repository ou persistência |

A Infrastructure só declara interfaces técnicas próprias da execução de agentes
(`IAgentExecutableResolver`, `IAgentProcessExecutor`, `IInteractiveAgentProcessLauncher`).
Cada `<Entidade>Repository` em `Modulos/<Modulo>/` implementa a porta
`I<Entidade>Repository` de `Dante.Application.<Modulo>`, e toda porta da Application
tem adapter registrado por `AddInfrastructure` quando há conexão.

## Quando criar AppService/fachada

Crie `<Conceito>AppService` na raiz da feature quando um host, um adapter ou outra
feature precisa executar uma operação que envolve autorização, transação ou ports.
O AppService é a fronteira: valida a entrada pelo validator, aplica regra contextual,
chama o Domain e persiste pela unidade de trabalho. A interface
`I<Conceito>AppService` existe quando outras features ou testes consomem o serviço
por contrato (os módulos CRUD: espaços, projetos, conhecimentos, relações e
captura); os demais AppServices são registrados pelo tipo concreto.

Uma fachada (`ConversaDoBrainAppService`) é um AppService que só orquestra: autoriza,
resolve a intenção, escolhe o caso de uso e mantém o ciclo de confirmação, delegando
a execução aos casos de uso da feature.

## Quando extrair caso de uso interno

Extraia para `CasosDeUso/` quando o AppService acumula fluxos distintos, cada um com
dependências próprias, ou quando um algoritmo pode ser testado sem ports:

| Tipo | Forma | Exemplos |
| --- | --- | --- |
| Caso de uso com dependências | classe pública `sealed`, dependências por construtor, métodos de execução `internal`, registrada scoped em `ServicosDoBrain` | `ConsultaDaConversa`, `CapturaDaConversa` |
| Política pura | classe `internal static` sem DI, recebe dados já autorizados e instantes explícitos | `AnaliseDeQualidade`, `SelecaoDeContexto`, `AgregacaoDeMetricas` |

Não extraia um método usado uma única vez e sem dependência própria: ele continua no
AppService. Estado de uma chamada (`ContextoDaConversa`) é passado como parâmetro e
nunca registrado no container. Ao extrair o primeiro caso de uso, o módulo simples
passa a complexo e seus contratos/ports vão para as subpastas no mesmo PR.

## Criar um módulo novo

1. Entidade e invariantes no módulo homônimo do Domain, com Guid Id via `EntidadeBase`.
2. Na Application, pasta `<Feature>/` com namespace `Dante.Application.<Feature>`:
   AppService (e interface, se consumido por contrato), DTO/SearchDto,
   `I<Entidade>Repository`, `<Entidade>Mapping` e `<Conceito>Validator`, um tipo por
   arquivo, na raiz enquanto o módulo for simples.
3. Mapping `internal static` registrado na lista de `MapeamentosDaApplication`;
   validator `internal static` chamado pelo AppService.
4. AppService/casos de uso na lista de `Composicao/ServicosDoBrain`.
5. Adapter/repository e `DbMapping` na Infrastructure ([infrastructure](infrastructure.md)).
6. Atualize a tabela de classificação acima e rode a suíte: os testes abaixo recusam
   arquivo fora do lugar, classificação divergente e serviço sem composição.

## Regras executáveis

| Regra | Teste |
| --- | --- |
| Application referencia só Domain, Mapster e abstrações de DI; sem EF Core/Npgsql/hosts | `HexagonalArchitectureTests`, `ApplicationArchitectureTests.ApplicationReferenciaSomenteDomainEAbstracoesSuportadas` |
| Domain sem interfaces, mapper, validator, repository, DTO ou referência fora do runtime | `ApplicationArchitectureTests.DomainNaoTemMapperValidatorPortaNemProviderTecnico` |
| Ports na Application, implementadas e registradas pela Infrastructure | `ApplicationArchitectureTests.PortasFicamNaApplicationEInfrastructureSoAsImplementa` |
| Estrutura física de módulo simples/complexo e tabela de classificação | `FeaturesSeguemAEstruturaDocumentada`, `ClassificacaoDocumentadaCorrespondeAsPastas` |
| Namespace da feature dentro de subpastas | `HexagonalArchitectureTests.ApplicationTypesKeepTheFeatureNamespaceInsideSubfolders` |
| Mappings `internal static` na feature do próprio DTO; composição sem expressões | `MapeamentoTests.MappingsFicamNaFeatureEAComposicaoNaoConheceDetalhes`, `ApplicationArchitectureTests.MappingsFicamNaFeatureDoProprioDto` |
| Validators `internal static` na feature | `ApplicationArchitectureTests.ValidatorsSaoEstaticosInternosDaFeature` |
| Hosts sem port implementada, AppService, validator, mapping ou registro da Application | `ApplicationArchitectureTests.HostsNaoImplementamRegrasDaApplication` |
| Infrastructure não registra AppServices | `ApplicationCompositionTests.InfrastructureNaoRegistraImplementacoesDaApplication` |
| `AddApplication` idempotente e preserva registros anteriores; sem duplicação com `AddInfrastructure` | `ApplicationCompositionTests.AddApplicationEIdempotenteEPreservaRegistrosAnteriores`, `ApplicationRegistraUmaVezEResolveTodosOsServicosComAdaptersConfigurados` |
| Todo serviço resolve com fakes dos ports, sem Infrastructure | `ApplicationCompositionTests.ApplicationResolveTodosOsServicosComFakesDosPorts` |
| Lifetimes: mapping/policy singleton, demais scoped, nenhum transient | `ApplicationCompositionTests.LifetimesDaApplicationSaoCoerentes` |
