using System.Collections.Concurrent;
using Dante.Application.ConversaDoBrain;
using Dante.Application.SegurancaDoBrain;
namespace Dante.Infrastructure.ConversaDoBrain;

public sealed class EstadoDeConversaDoBrainEmMemoria : IEstadoDeConversaDoBrain
{
    private readonly ConcurrentDictionary<ChaveDeConversaDto,EstadoDeConversaDto> estados=[];
    public EstadoDeConversaDto Obter(ChaveDeConversaDto chave)
    {
        if(estados.TryGetValue(chave,out var estado) && estado.AtualizadoEm>DateTimeOffset.UtcNow.AddMinutes(-10))
            return estado.Pendente is { } proposta && (proposta.ExpiraEm is null || proposta.ExpiraEm<=DateTimeOffset.UtcNow)?estado with{Pendente=null,ConfirmacaoExpirada=true}:estado;
        estados.TryRemove(chave,out _);return new(Guid.NewGuid(),[],null,null,DateTimeOffset.UtcNow);
    }
    public EstadoDeConversaDto Salvar(ChaveDeConversaDto chave,EstadoDeConversaDto estado)
    {
        if(estados.Count>=1000)
        {
            foreach(var item in estados.Where(x=>x.Value.AtualizadoEm<=DateTimeOffset.UtcNow.AddMinutes(-10)))estados.TryRemove(item.Key,out _);
            if(!estados.ContainsKey(chave) && estados.Count>=1000)throw new InvalidOperationException("Muitas conversas Brain ativas.");
        }
        estado=estado with{Versao=Guid.NewGuid(),AtualizadoEm=DateTimeOffset.UtcNow,
            ContextoDeAlteracao=estado.ContextoDeAlteracao||estado.Pendente is not null,
            ConfirmacaoExpirada=estado.Pendente is not null?false:estado.ConfirmacaoExpirada};estados[chave]=estado;return estado;
    }
    public bool ConsumirPendente(ChaveDeConversaDto chave,Guid versao)
    {
        if(!estados.TryGetValue(chave,out var atual) || atual.Versao!=versao || atual.Pendente is null || (atual.Pendente.ExpiraEm is null || atual.Pendente.ExpiraEm<=DateTimeOffset.UtcNow))return false;
        return estados.TryUpdate(chave,atual with{Pendente=null,Versao=Guid.NewGuid()},atual);
    }
    public void LimparConversa(IdentidadeDoBrain identidade,string idConversa)
    {foreach(var chave in estados.Keys.Where(x=>x.IdTenant==identidade.IdTenant && x.IdUsuario==identidade.IdUsuario && x.IdConversa==idConversa))estados.TryRemove(chave,out _);}
}
