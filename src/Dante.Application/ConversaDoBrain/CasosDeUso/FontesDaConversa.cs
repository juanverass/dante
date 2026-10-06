using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class FontesDaConversa(DocumentoFonteAppService documentos, IDocumentoFonteRepository fontes)
{
    internal async Task<string> ImportarFonteAsync(ContextoDaConversa conversa, IntencaoResolvidaDto intencao, CancellationToken cancellationToken)
    {
        var origemFonte="nota:"+intencao.Nome;ProtecaoDeSegredos.GarantirSeguro(origemFonte,intencao.Texto);
        var existente=await fontes.ObterPelaOrigemAsync(conversa.Acesso,origemFonte,cancellationToken);
        if(existente is null)
        {
            await documentos.ImportarAsync(conversa.Acesso,origemFonte,intencao.Formato!,intencao.Texto,cancellationToken:cancellationToken);
            return "Fonte bruta adicionada. Importar não confirma conteúdo; consulte pelo assunto e selecione um trecho para preparar candidato.";
        }
        if(existente.Formato!=intencao.Formato)throw new ArgumentException("Formato da fonte mudou.");
        var alvoFonte=new AlvoDeConversaDto(existente.Id,existente.Revisao,"fonte_bruta",null,null,existente.Sensibilidade,Resumir(existente.Origem,500));
        conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("fonte",alvoFonte,intencao.Texto)});
        return $"A fonte {alvoFonte.Descricao} já existe. Vou atualizar a revisão e reconstruir os trechos, preservando conhecimento consolidado. Diga confirmar ou cancelar.";
    }
    internal async Task<string> CapturarFonteAsync(ContextoDaConversa conversa, IntencaoResolvidaDto intencao, CancellationToken cancellationToken)
    {
        var selecionada=conversa.Escolher(intencao.Numero);
        if(selecionada is not {Origem:"fonte_bruta",NumeroDaParte:not null})return "Escolha o número de uma fonte bruta na última consulta.";
        var candidatoFonte=await documentos.GerarCandidatoAsync(conversa.Acesso,selecionada.Id,selecionada.Revisao,selecionada.NumeroDaParte.Value,cancellationToken:cancellationToken);
        if(candidatoFonte.Estado!=EstadoDoCandidato.Pendente)return "Esse trecho já foi avaliado; não criei candidato duplicado.";
        var candidatoAlvo=new AlvoDeConversaDto(candidatoFonte.Id,candidatoFonte.Revisao,"candidato",candidatoFonte.Tipo,null,candidatoFonte.Sensibilidade,Resumir(candidatoFonte.Conteudo,600));
        conversa.Guardar(conversa.Estado with{Pendente=conversa.Pendente("captura",candidatoAlvo)});
        return $"Preparei candidato do trecho exato: {candidatoAlvo.Descricao}\nProveniência inclui documento/revisão/hash/parte. Diga confirmar ou cancelar.";
    }
}
