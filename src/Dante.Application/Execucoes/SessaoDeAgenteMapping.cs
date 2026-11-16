using Dante.Application.Mapeamento;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Só leitura: o registro da sessão muda apenas pelos métodos do domínio.
internal static class SessaoDeAgenteMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<SessaoDeAgente, SessaoDeAgenteDto>(sessao => new SessaoDeAgenteDto
        {
            Id = sessao.Id,
            Codigo = sessao.Codigo,
            Numero = sessao.Numero,
            Agente = sessao.Agente,
            Estado = sessao.Estado,
            Canal = sessao.Canal,
            IdDoDono = sessao.IdDoDono,
            Contexto = sessao.Contexto,
            Perfil = sessao.Perfil,
            Modelo = sessao.Modelo,
            Esforco = sessao.Esforco,
            ModeloInformado = sessao.ModeloInformado,
            IdUpstream = sessao.IdUpstream,
            IdExecucao = sessao.IdExecucao,
            HandleDoAgente = sessao.HandleDoAgente,
            HashDaConfiguracaoDoAgente = sessao.HashDaConfiguracaoDoAgente,
            IdVersaoDoAgente = sessao.IdVersaoDoAgente,
            MotivoDoFim = sessao.MotivoDoFim,
            CriadaEm = sessao.CriadaEm,
            IniciadaEm = sessao.IniciadaEm,
            EncerradaEm = sessao.EncerradaEm
        });
}
