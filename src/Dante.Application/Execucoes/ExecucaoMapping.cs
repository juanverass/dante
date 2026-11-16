using Dante.Application.Mapeamento;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Só leitura: execuções, etapas, dependências e eventos nascem pelos métodos do domínio, nunca de DTO.
internal static class ExecucaoMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao)
    {
        configuracao.Registrar<Execucao, ExecucaoDto>(execucao => new ExecucaoDto
        {
            Id = execucao.Id,
            Codigo = execucao.Codigo,
            Numero = execucao.Numero,
            Tipo = execucao.Tipo,
            Estado = execucao.Estado,
            Canal = execucao.Canal,
            IdDoUsuarioNoCanal = execucao.IdDoUsuarioNoCanal,
            Contexto = execucao.Contexto,
            Objetivo = execucao.Objetivo,
            Erro = execucao.Erro,
            Motivo = execucao.Motivo,
            CriadaEm = execucao.CriadaEm,
            IniciadaEm = execucao.IniciadaEm,
            EncerradaEm = execucao.EncerradaEm
        });
        configuracao.Registrar<EtapaDaExecucao, EtapaDaExecucaoDto>(etapa => new EtapaDaExecucaoDto
        {
            Id = etapa.Id,
            Numero = etapa.Numero,
            Tipo = etapa.Tipo,
            Estado = etapa.Estado,
            Escrita = etapa.Escrita,
            PoliticaDeRetomada = etapa.PoliticaDeRetomada,
            Tentativa = etapa.Tentativa,
            ChaveDeIdempotencia = etapa.ChaveDeIdempotencia,
            Agente = etapa.Agente,
            Modelo = etapa.Modelo,
            Esforco = etapa.Esforco,
            IdSessaoDeAgente = etapa.IdSessaoDeAgente,
            IdTurno = etapa.IdTurno,
            CodigoDeSaida = etapa.CodigoDeSaida,
            Erro = etapa.Erro,
            Motivo = etapa.Motivo,
            CriadaEm = etapa.CriadaEm,
            IniciadaEm = etapa.IniciadaEm,
            EncerradaEm = etapa.EncerradaEm
        });
        configuracao.Registrar<DependenciaDeEtapa, DependenciaDeEtapaDto>(dependencia =>
            new DependenciaDeEtapaDto(dependencia.IdEtapa, dependencia.IdEtapaRequerida));
        configuracao.Registrar<EventoDeOrquestracao, EventoDeOrquestracaoDto>(evento => new EventoDeOrquestracaoDto
        {
            Sequencia = evento.Sequencia,
            Tipo = evento.Tipo,
            IdExecucao = evento.IdExecucao,
            IdEtapa = evento.IdEtapa,
            IdSessaoDeAgente = evento.IdSessaoDeAgente,
            IdTurno = evento.IdTurno,
            Dados = evento.Dados,
            OcorridoEm = evento.OcorridoEm
        });
    }
}
