# Mappings da Application (AD-37, #169)

Mapster 10.0.13 é o mapper padrão da Application. Domain não tem dependência do
pacote. Worker/WebApi usam `AddApplication()` e não definem conversões próprias.
AppServices recebem `IMapsterTypeAdapter` por DI; a #171 pode usar esse contrato
na base CRUD sem depender de um host ou de configuração estática do Mapster
(ver [base CRUD](crud.md)).

## Registro e operação

Mappings funcionais ficam na feature dona do conceito (#204): uma classe
`internal static <Entidade>Mapping` com `Registrar(ConfiguracaoMapeamento)`, na raiz da
pasta da feature (`Projetos/ProjetoMapping.cs`, `Conhecimentos/ConhecimentoMapping.cs`).
Projeções reutilizadas por outras features ficam no mapping do dono do tipo
(`ConhecimentoMapping.ParaProvenienciaDto`). `Mapeamento/MapeamentosDaApplication` só
compõe: chama o `Registrar` de cada feature, numa lista explícita, sem expressões
próprias nem referência ao Domain. Feature nova com mapping cria seu `<Entidade>Mapping`
e entra nessa lista.

O ponto explícito `AddMapeamentos(Action<ConfiguracaoMapeamento>)` permite registrar
mappings externos à Application na composição, sem duplicação entre hosts. Não fazer
scan indiscriminado de assemblies, descoberta por reflexão nem usar
`TypeAdapterConfig.GlobalSettings`.

Cada **par e direção** exige `Registrar<TOrigem,TDestino>(expressao)`. A expressão
é a projeção/fábrica completa, compilada pelo Mapster; nenhum campo é copiado por
convenção de nomes. Exemplo ilustrativo, sem criar entidades/DTOs futuros:

```csharp
configuracao.Registrar<Projeto, ProjetoDto>(projeto =>
    new ProjetoDto { Id = projeto.Id, Nome = projeto.Nome });
```

Somente os campos escolhidos entram no resultado. Campos sensíveis/internos,
propriedades novas e IDs de isolamento permanecem omitidos até configuração
explícita. Expor um Id no DTO de saída pode ser parte do contrato; não implica
aceitar esse Id como autoridade na entrada. Mappings são configuração confiável
de Application, revisada com seus contratos, e não policy de autorização.

O adapter oferece `Mapear<TOrigem,TDestino>(origem)`. Par não registrado, direção
inversa implícita, automapping do mesmo tipo, lista ou SearchDto sem configuração
são recusados. Null é recusado. Configuração é compilada/finalizada ao resolver o
singleton; registros duplicados ou alterações após finalização são recusados.
Instâncias são isoladas por service provider, seguras para leitura concorrente.

## Identidade e invariantes

DTO de criação é um contrato de entrada com campos permitidos, normalmente sem
Id/IDs internos. DTO de saída pode expor identidade conforme o caso de uso.
DTOs/SearchDtos específicos usam PT-BR: EspacoDeConhecimentoDto,
EspacoDeConhecimentoSearchDto, ProjetoDto, ProjetoSearchDto, ConhecimentoDto e
ConhecimentoSearchDto, cada um na sua feature.

Entrada → entidade exige fábrica/constructor público do domínio que valida
invariantes e gera Guid Id. Não aceitar Id do DTO automaticamente, não selecionar
constructor sem validação nem expor setters para facilitar o mapper. Se um caso
futuro permitir importar identidade, isso exige contrato/caso de uso específico.

O adapter **não oferece map-to-target nem atualização sobre entidade existente**.
Atualização é: AppService carrega entidade pelo Id/escopo autorizado → valida DTO →
chama métodos do domínio. O mapper não pode substituir métodos/regras do domínio
por cópia de propriedades. Não implementar entidades/persistência para demonstrar
mapping nesta fundação; os testes usam contratos locais fictícios.

SearchDto define filtros/limites explícitos validados pelo AppService; não vira
IQueryable/SQL/expressão arbitrária por automapping. Mapping de forma não autoriza
acesso a conteúdo: policy/escopo continuam no caso de uso.

## Validação

`MapeamentoTests` cobre projeção explícita, omissão de campos sensíveis/Ids internos,
criação pelo constructor com nova identidade e invariantes, recusa de pares/direções
não registrados, configuração imutável/duplicada, isolamento/concurrency e injeção
em AppService. `AddApplicationRegistraOsMappingsDeCadaFeature` confirma os pares
funcionais compostos por `AddApplication`, e
`MappingsFicamNaFeatureEAComposicaoNaoConheceDetalhes` exige cada `<Entidade>Mapping`
na sua feature e a composição sem expressões. Os testes arquiteturais continuam
impedindo Mapster no Domain.

Referências primárias: [MapWith](https://github.com/MapsterMapper/Mapster/wiki/Custom-conversion-logic)
e [configuração/compilação](https://github.com/MapsterMapper/Mapster/wiki/Config-validation-%26-compilation).
