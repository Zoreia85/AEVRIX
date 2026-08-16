# Inventário de versões AEVRIX

Este documento registra somente versões e artefatos cuja existência pode ser comprovada por histórico ou evidência técnica recuperada.

## 0.1.0 RC1

**Estado:** histórico / reprovado para homologação.

**Alvo:** Windows x86-64.

Artefatos comprovados pela auditoria de 12/08/2026:

| Artefato | Tamanho | SHA-256 | Situação no repositório |
|---|---:|---|---|
| `AEVRIX_Setup_0.1.0_RC1.exe` | 8.413.184 bytes | `eb81c734fd505e8de973f0de75ca5cf936d437cc818891e84e9d914a2703d80f` | binário original ainda não recuperado |
| `AEVRIX.exe` | 6.568.448 bytes | `1da2dd80b74580a070feef043c938e642266a1730a67f048f79fe86eb491095d` | payload original ainda não recuperado |
| `AEVRIX_Authority.exe` | 2.380.800 bytes | `5b2865d4d6c5834ad9e95af8bc529927e62822f76df96bc7aa08484806a02db3` | não publicar até remover dependência de segredo histórico |
| `AEVRIX_OWNER_AUTHORITY_RC1.zip` | 1.040.446 bytes | `1a0527f80065662705cb69779bf24a9b3a1b24cbc12621c2664922620c16685a` | proibido publicar: continha segredo privado |

### Decisão de auditoria

**NO-GO — REPROVADA PARA RELEASE CANDIDATE E NÃO HOMOLOGADA.**

A versão apresentava, entre outros pontos, ausência de Authenticode, falta de teste dinâmico em Windows real, runtime Go antigo, insuficiência de supply-chain/reprodutibilidade, retenção de dados sensíveis fora do vault e exposição histórica de chave privada no pacote de autoridade.

## 0.2 DEV

**Estado:** linha de desenvolvimento posterior identificada no histórico do projeto.

Características já registradas no histórico:

- V0.2 DEV preservada como linha de evolução após a RC1;
- suíte anterior com 37/37 testes em PASS em seu contexto de desenvolvimento;
- Release Guard impedindo promoção prematura para RC;
- Control Plane, Brain Capsule, Offline Replica, ARP, Tool Hub e mecanismos de aprendizado autoral implementados em nível de referência;
- pendências maiores: Research Browser real, adapters externos, LLM local de produção, build Windows final, assinatura e testes reais em Windows.

O código/artefatos exatos dessa linha ainda estão sendo recuperados. Qualquer reconstrução futura deverá ser identificada como reconstrução e não como binário original.

## Política de integridade histórica

1. Nunca inventar uma versão ou hash.
2. Nunca substituir um binário histórico por recompilação sem alterar a identificação.
3. Toda versão recuperada deve registrar origem, hash, estado de homologação e limitações.
4. Segredos, dados pessoais e material privado são excluídos mesmo quando pertenciam ao pacote histórico original.
