namespace LayoutParserApi.Services.Interfaces
{
    public interface IDecryptionService
    {
        /// <summary>
        /// Descriptografa o conteúdo via executável legado (.NET Framework 4.8.1), de forma
        /// assíncrona para não bloquear thread do pool no caminho do <c>SqlDataReader</c>.
        ///
        /// <para><b>Falha explícito</b>: lança <see cref="LayoutParserApi.Services.Database.DecryptionException"/>
        /// quando o executável não existe, sai com código != 0 ou estoura o timeout — nunca devolve
        /// a cifra como se fosse texto claro (P1.1 do plano de segurança). Entrada vazia devolve
        /// string vazia (não é falha).</para>
        /// </summary>
        Task<string> DecryptContentAsync(string encryptedContent);

        /// <summary>
        /// Indica se o executável de descriptografia foi localizado e existe em disco. Usado pela
        /// sonda de readiness (health check) — sem o decryptor o catálogo volta vazio.
        /// </summary>
        bool IsDecryptorAvailable { get; }
    }
}
