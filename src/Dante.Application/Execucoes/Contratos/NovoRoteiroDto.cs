using Dante.Application.Contextos;
using Dante.Application.Grupos;

namespace Dante.Application.Execucoes;

// Pedido de uma tarefa a um grupo (#308): a resolução do grupo (versão ativa e versão atual de cada participante, já
// conferidas), o objetivo bruto (redigido antes de qualquer gravação; o bruto nunca é persistido), o contexto de
// repositório e a origem. Número já reservado; a chave deduplica a mensagem da interface.
public sealed record NovoRoteiroDto(ResolucaoDeGrupoDto Grupo, string Objetivo, JobExecutionContext Contexto, long Numero,
    string Canal, long IdDoUsuarioNoCanal, string ChaveDeDeduplicacao);
