namespace LayoutParserApi.Services.Database
{
    /// <summary>
    /// Falha REAL de descriptografia (executável ausente, saída != 0, timeout, erro de I/O).
    ///
    /// <para>Existe para o chamador distinguir "não descriptografou" de "descriptografou e não é
    /// TextPositional". Antes o <see cref="DecryptionService"/> devolvia o texto cifrado como se
    /// fosse válido (P1.1 do plano de segurança): o catálogo voltava vazio/parcial com 200 e o
    /// operador concluía "não há layouts". Agora falha EXPLÍCITA — o warm-up conta como falha e
    /// os endpoints test-decryption* respondem 500/503 em vez de ecoar a cifra com success=true.</para>
    /// </summary>
    public class DecryptionException : Exception
    {
        public DecryptionException(string message) : base(message) { }
        public DecryptionException(string message, Exception innerException) : base(message, innerException) { }
    }
}
