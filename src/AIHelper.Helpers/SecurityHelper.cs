using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIHelper.Helpers;

public static class SecurityHelper
{
	private static readonly string Key = "XianyuStockHelper2026SecretKey88";

	private static readonly string Iv = "1234567890123456";

	public static string Decrypt(string encryptedText)
	{
		try
		{
			if (string.IsNullOrEmpty(encryptedText))
			{
				return "";
			}
			byte[] buffer = Convert.FromBase64String(encryptedText);
			using Aes aes = Aes.Create();
			aes.Key = Encoding.UTF8.GetBytes(Key);
			aes.IV = Encoding.UTF8.GetBytes(Iv);
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using ICryptoTransform transform = aes.CreateDecryptor();
			using MemoryStream stream = new MemoryStream(buffer);
			using CryptoStream stream2 = new CryptoStream(stream, transform, CryptoStreamMode.Read);
			using StreamReader streamReader = new StreamReader(stream2);
			return streamReader.ReadToEnd();
		}
		catch
		{
			return "";
		}
	}

	public static string Encrypt(string plainText)
	{
		using Aes aes = Aes.Create();
		aes.Key = Encoding.UTF8.GetBytes(Key);
		aes.IV = Encoding.UTF8.GetBytes(Iv);
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		using ICryptoTransform transform = aes.CreateEncryptor();
		using MemoryStream memoryStream = new MemoryStream();
		using (CryptoStream stream = new CryptoStream(memoryStream, transform, CryptoStreamMode.Write))
		{
			using StreamWriter streamWriter = new StreamWriter(stream);
			streamWriter.Write(plainText);
		}
		return Convert.ToBase64String(memoryStream.ToArray());
	}
}
