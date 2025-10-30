using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CryptographySysMiddle
{
    public class Cryptography
    {
        private static readonly SymmetricAlgorithm SymmetricAlgorithm = new RijndaelManaged();

        public static string Decrypt(string messageToDecrypt)
        {
            ICryptoTransform cryptoTransform = SymmetricAlgorithm.CreateDecryptor(Encoding.UTF8.GetBytes("dbc%$#h92785"), Encoding.UTF8.GetBytes("Ca#&UjO){Qwz*@FcsPs"));
            byte[] inputBytes = Convert.FromBase64String(messageToDecrypt);
            byte[] bytes = Operation(cryptoTransform, inputBytes);
            return Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Operation(ICryptoTransform cryptoTransform, byte[] inputBytes)
        {
            MemoryStream memoryStream = new MemoryStream();
            CryptoStream cryptoStream = new CryptoStream(memoryStream, cryptoTransform, CryptoStreamMode.Write);
            cryptoStream.Write(inputBytes, 0, inputBytes.Length);
            cryptoStream.Close();
            return memoryStream.ToArray();
        }
    }
}
