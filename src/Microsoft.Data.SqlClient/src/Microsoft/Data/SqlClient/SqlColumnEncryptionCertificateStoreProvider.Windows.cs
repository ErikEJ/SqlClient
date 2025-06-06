﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Microsoft.Data.SqlClient
{
    /// <include file='../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlColumnEncryptionCertificateStoreProvider.xml' path='docs/members[@name="SqlColumnEncryptionCertificateStoreProvider"]/SqlColumnEncryptionCertificateStoreProvider/*' />
    public class SqlColumnEncryptionCertificateStoreProvider : SqlColumnEncryptionKeyStoreProvider
    {
        // Constants
        //
        // Assumption: Certificate Locations (LocalMachine & CurrentUser), Certificate Store name "My"
        // Certificate provider name (CertificateStore) dont need to be localized.

        /// <include file='../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlColumnEncryptionCertificateStoreProvider.xml' path='docs/members[@name="SqlColumnEncryptionCertificateStoreProvider"]/ProviderName/*' />
        public const string ProviderName = @"MSSQL_CERTIFICATE_STORE";

        /// <summary>
        /// RSA_OAEP is the only algorithm supported for encrypting/decrypting column encryption keys.
        /// </summary>
        internal const string RSAEncryptionAlgorithmWithOAEP = @"RSA_OAEP";

        /// <summary>
        /// LocalMachine certificate store location. Valid certificate locations are LocalMachine and CurrentUser.
        /// </summary>
        private const string CertLocationLocalMachine = @"LocalMachine";

        /// <summary>
        /// CurrentUser certificate store location. Valid certificate locations are LocalMachine and CurrentUser.
        /// </summary>
        private const string CertLocationCurrentUser = @"CurrentUser";

        /// <summary>
        /// Valid certificate store
        /// </summary>
        private const string MyCertificateStore = @"My";

        /// <summary>
        /// Hashing algorithm used for signing
        /// </summary>
        private const string HashingAlgorithm = @"SHA256";

        /// <summary>
        /// Algorithm version
        /// </summary>
        private readonly byte[] _version = new byte[] { 0x01 };

        /// <include file='../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlColumnEncryptionCertificateStoreProvider.xml' path='docs/members[@name="SqlColumnEncryptionCertificateStoreProvider"]/DecryptColumnEncryptionKey/*' />
        public override byte[] DecryptColumnEncryptionKey(string masterKeyPath, string encryptionAlgorithm, byte[] encryptedColumnEncryptionKey)
        {
            // Validate the input parameters
            ValidateNonEmptyCertificatePath(masterKeyPath, isSystemOp: true);

            if (encryptedColumnEncryptionKey == null)
            {
                throw SQL.NullEncryptedColumnEncryptionKey();
            }
            else if (0 == encryptedColumnEncryptionKey.Length)
            {
                throw SQL.EmptyEncryptedColumnEncryptionKey();
            }

            // Validate encryptionAlgorithm
            ValidateEncryptionAlgorithm(encryptionAlgorithm, isSystemOp: true);

            // Validate key path length
            ValidateCertificatePathLength(masterKeyPath, isSystemOp: true);

            // Parse the path and get the X509 cert
            X509Certificate2 certificate = GetCertificateByPath(masterKeyPath, isSystemOp: true);
            
            RSA RSAPublicKey = certificate.GetRSAPublicKey();
            int keySizeInBytes= RSAPublicKey.KeySize / 8;

            // Validate and decrypt the EncryptedColumnEncryptionKey
            // Format is 
            //           version + keyPathLength + ciphertextLength + keyPath + ciphertext +  signature
            //
            // keyPath is present in the encrypted column encryption key for identifying the original source of the asymmetric key pair and 
            // we will not validate it against the data contained in the CMK metadata (masterKeyPath).

            // Validate the version byte
            if (encryptedColumnEncryptionKey[0] != _version[0])
            {
                throw SQL.InvalidAlgorithmVersionInEncryptedCEK(encryptedColumnEncryptionKey[0], _version[0]);
            }

            // Get key path length
            int currentIndex = _version.Length;
            short keyPathLength = BitConverter.ToInt16(encryptedColumnEncryptionKey, currentIndex);
            currentIndex += sizeof(short);

            // Get ciphertext length
            int cipherTextLength = BitConverter.ToInt16(encryptedColumnEncryptionKey, currentIndex);
            currentIndex += sizeof(short);

            // Skip KeyPath
            // KeyPath exists only for troubleshooting purposes and doesnt need validation.
            currentIndex += keyPathLength;

            // validate the ciphertext length
            if (cipherTextLength != keySizeInBytes)
            {
                throw SQL.InvalidCiphertextLengthInEncryptedCEK(cipherTextLength, keySizeInBytes, masterKeyPath);
            }

            // Validate the signature length
            // Signature length should be same as the key side for RSA PKCSv1.5
            int signatureLength = encryptedColumnEncryptionKey.Length - currentIndex - cipherTextLength;
            if (signatureLength != keySizeInBytes)
            {
                throw SQL.InvalidSignatureInEncryptedCEK(signatureLength, keySizeInBytes, masterKeyPath);
            }

            // Get ciphertext
            byte[] cipherText = new byte[cipherTextLength];
            Buffer.BlockCopy(encryptedColumnEncryptionKey, currentIndex, cipherText, 0, cipherText.Length);
            currentIndex += cipherTextLength;

            // Get signature
            byte[] signature = new byte[signatureLength];
            Buffer.BlockCopy(encryptedColumnEncryptionKey, currentIndex, signature, 0, signature.Length);

            // Compute the hash to validate the signature
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                sha256.TransformFinalBlock(encryptedColumnEncryptionKey, 0, encryptedColumnEncryptionKey.Length - signature.Length);
                hash = sha256.Hash;
            }

            Debug.Assert(hash != null, @"hash should not be null while decrypting encrypted column encryption key.");

            // Validate the signature
            if (!RSAVerifySignature(hash, signature, certificate))
            {
                throw SQL.InvalidCertificateSignature(masterKeyPath);
            }

            // Decrypt the CEK
            return RSADecrypt(cipherText, certificate);
        }

        /// <include file='../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlColumnEncryptionCertificateStoreProvider.xml' path='docs/members[@name="SqlColumnEncryptionCertificateStoreProvider"]/EncryptColumnEncryptionKey/*' />
        public override byte[] EncryptColumnEncryptionKey(string masterKeyPath, string encryptionAlgorithm, byte[] columnEncryptionKey)
        {
            // Validate the input parameters
            ValidateNonEmptyCertificatePath(masterKeyPath, isSystemOp: false);
            if (columnEncryptionKey == null)
            {
                throw SQL.NullColumnEncryptionKey();
            }
            else if (0 == columnEncryptionKey.Length)
            {
                throw SQL.EmptyColumnEncryptionKey();
            }

            // Validate encryptionAlgorithm
            ValidateEncryptionAlgorithm(encryptionAlgorithm, isSystemOp: false);

            // Validate masterKeyPath Length
            ValidateCertificatePathLength(masterKeyPath, isSystemOp: false);

            // Parse the certificate path and get the X509 cert
            X509Certificate2 certificate = GetCertificateByPath(masterKeyPath, isSystemOp: false);
            
            RSA RSAPublicKey = certificate.GetRSAPublicKey();
            int keySizeInBytes = RSAPublicKey.KeySize / 8;

            // Construct the encryptedColumnEncryptionKey
            // Format is 
            //          version + keyPathLength + ciphertextLength + ciphertext + keyPath + signature
            //
            // We currently only support one version
            byte[] version = new byte[] { _version[0] };

            // Get the Unicode encoded bytes of culture invariant lower case masterKeyPath
            byte[] masterKeyPathBytes = Encoding.Unicode.GetBytes(masterKeyPath.ToLowerInvariant());
            byte[] keyPathLength = BitConverter.GetBytes((short)masterKeyPathBytes.Length);

            // Encrypt the plain text
            byte[] cipherText = RSAEncrypt(columnEncryptionKey, certificate);
            byte[] cipherTextLength = BitConverter.GetBytes((short)cipherText.Length);
            Debug.Assert(cipherText.Length == keySizeInBytes, @"cipherText length does not match the RSA key size");

            // Compute hash
            // SHA-2-256(version + keyPathLength + ciphertextLength + keyPath + ciphertext) 
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                sha256.TransformBlock(version, 0, version.Length, version, 0);
                sha256.TransformBlock(keyPathLength, 0, keyPathLength.Length, keyPathLength, 0);
                sha256.TransformBlock(cipherTextLength, 0, cipherTextLength.Length, cipherTextLength, 0);
                sha256.TransformBlock(masterKeyPathBytes, 0, masterKeyPathBytes.Length, masterKeyPathBytes, 0);
                sha256.TransformFinalBlock(cipherText, 0, cipherText.Length);
                hash = sha256.Hash;
            }

            // Sign the hash
            byte[] signedHash = RSASignHashedData(hash, certificate);
            Debug.Assert(signedHash.Length == keySizeInBytes, @"signed hash length does not match the RSA key size");
            Debug.Assert(RSAVerifySignature(hash, signedHash, certificate), @"Invalid signature of the encrypted column encryption key computed.");

            // Construct the encrypted column encryption key
            // EncryptedColumnEncryptionKey = version + keyPathLength + ciphertextLength + keyPath + ciphertext +  signature
            int encryptedColumnEncryptionKeyLength = version.Length + cipherTextLength.Length + keyPathLength.Length + cipherText.Length + masterKeyPathBytes.Length + signedHash.Length;
            byte[] encryptedColumnEncryptionKey = new byte[encryptedColumnEncryptionKeyLength];

            // Copy version byte
            int currentIndex = 0;
            Buffer.BlockCopy(version, 0, encryptedColumnEncryptionKey, currentIndex, version.Length);
            currentIndex += version.Length;

            // Copy key path length
            Buffer.BlockCopy(keyPathLength, 0, encryptedColumnEncryptionKey, currentIndex, keyPathLength.Length);
            currentIndex += keyPathLength.Length;

            // Copy ciphertext length
            Buffer.BlockCopy(cipherTextLength, 0, encryptedColumnEncryptionKey, currentIndex, cipherTextLength.Length);
            currentIndex += cipherTextLength.Length;

            // Copy key path
            Buffer.BlockCopy(masterKeyPathBytes, 0, encryptedColumnEncryptionKey, currentIndex, masterKeyPathBytes.Length);
            currentIndex += masterKeyPathBytes.Length;

            // Copy ciphertext
            Buffer.BlockCopy(cipherText, 0, encryptedColumnEncryptionKey, currentIndex, cipherText.Length);
            currentIndex += cipherText.Length;

            // copy the signature
            Buffer.BlockCopy(signedHash, 0, encryptedColumnEncryptionKey, currentIndex, signedHash.Length);

            return encryptedColumnEncryptionKey;
        }

        /// <include file='../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlColumnEncryptionCertificateStoreProvider.xml' path='docs/members[@name="SqlColumnEncryptionCertificateStoreProvider"]/SignColumnMasterKeyMetadata/*' />
        public override byte[] SignColumnMasterKeyMetadata(string masterKeyPath, bool allowEnclaveComputations)
        {
            byte[] hash = ComputeMasterKeyMetadataHash(masterKeyPath, allowEnclaveComputations, isSystemOp: false);

            // Parse the certificate path and get the X509 cert
            X509Certificate2 certificate = GetCertificateByPath(masterKeyPath, isSystemOp: false);

            byte[] signature = RSASignHashedData(hash, certificate);

            return signature;
        }

        /// <include file='../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlColumnEncryptionCertificateStoreProvider.xml' path='docs/members[@name="SqlColumnEncryptionCertificateStoreProvider"]/VerifyColumnMasterKeyMetadata/*' />
        public override bool VerifyColumnMasterKeyMetadata(string masterKeyPath, bool allowEnclaveComputations, byte[] signature)
        {
            byte[] hash = ComputeMasterKeyMetadataHash(masterKeyPath, allowEnclaveComputations, isSystemOp: true);

            // Parse the certificate path and get the X509 cert
            X509Certificate2 certificate = GetCertificateByPath(masterKeyPath, isSystemOp: true);

            // Validate the signature
            return RSAVerifySignature(hash, signature, certificate);
        }

        private byte[] ComputeMasterKeyMetadataHash(string masterKeyPath, bool allowEnclaveComputations, bool isSystemOp)
        {
            // Validate the input parameters
            ValidateNonEmptyCertificatePath(masterKeyPath, isSystemOp);

            // Validate masterKeyPath Length
            ValidateCertificatePathLength(masterKeyPath, isSystemOp);

            string masterkeyMetadata = ProviderName + masterKeyPath + allowEnclaveComputations;
            masterkeyMetadata = masterkeyMetadata.ToLowerInvariant();
            byte[] masterkeyMetadataBytes = Encoding.Unicode.GetBytes(masterkeyMetadata.ToLowerInvariant());

            // Compute hash 
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                sha256.TransformFinalBlock(masterkeyMetadataBytes, 0, masterkeyMetadataBytes.Length);
                hash = sha256.Hash;
            }
            return hash;
        }

        /// <summary>
        /// This function validates that the encryption algorithm is RSA_OAEP and if it is not,
        /// then throws an exception
        /// </summary>
        /// <param name="encryptionAlgorithm">Asymmetric key encryption algorithm</param>
        /// <param name="isSystemOp"></param>
        private void ValidateEncryptionAlgorithm(string encryptionAlgorithm, bool isSystemOp)
        {
            // This validates that the encryption algorithm is RSA_OAEP
            if (encryptionAlgorithm == null)
            {
                throw SQL.NullKeyEncryptionAlgorithm(isSystemOp);
            }

            if (string.Equals(encryptionAlgorithm, RSAEncryptionAlgorithmWithOAEP, StringComparison.OrdinalIgnoreCase) != true)
            {
                throw SQL.InvalidKeyEncryptionAlgorithm(encryptionAlgorithm, RSAEncryptionAlgorithmWithOAEP, isSystemOp);
            }
        }

        /// <summary>
        /// Certificate path length has to fit in two bytes, so check its value against Int16.MaxValue
        /// </summary>
        /// <param name="masterKeyPath"></param>
        /// <param name="isSystemOp"></param>
        private void ValidateCertificatePathLength(string masterKeyPath, bool isSystemOp)
        {
            if (masterKeyPath.Length >= short.MaxValue)
            {
                throw SQL.LargeCertificatePathLength(masterKeyPath.Length, short.MaxValue, isSystemOp);
            }
        }

        /// <summary>
        /// Gets a string array containing Valid certificate locations.
        /// </summary>
        private string[] GetValidCertificateLocations()
        {
            return new string[2] { CertLocationLocalMachine, CertLocationCurrentUser };
        }

        /// <summary>
        /// Checks if the certificate path is Empty or Null (and raises exception if they are).
        /// </summary>
        private void ValidateNonEmptyCertificatePath(string masterKeyPath, bool isSystemOp)
        {
            if (string.IsNullOrWhiteSpace(masterKeyPath))
            {
                if (masterKeyPath == null)
                {
                    throw SQL.NullCertificatePath(GetValidCertificateLocations(), isSystemOp);
                }
                else
                {
                    throw SQL.InvalidCertificatePath(masterKeyPath, GetValidCertificateLocations(), isSystemOp);
                }
            }
        }

        /// <summary>
        /// Parses the given certificate path, searches in certificate store and returns a matching certificate
        /// </summary>
        /// <param name="keyPath">
        /// Certificate key path. Format of the path is [LocalMachine|CurrentUser]/[storename]/thumbprint
        /// </param>
        /// <param name="isSystemOp"></param>
        /// <returns>Returns the certificate identified by the certificate path</returns>
        private X509Certificate2 GetCertificateByPath(string keyPath, bool isSystemOp)
        {
            Debug.Assert(!string.IsNullOrEmpty(keyPath));

            // Assign default values for omitted fields
            StoreLocation storeLocation = StoreLocation.LocalMachine; // Default to Local Machine
            StoreName storeName = StoreName.My;
            string[] certParts = keyPath.Split('/');

            // Validate certificate path
            // Certificate path should only contain 3 parts (Certificate Location, Certificate Store Name and Thumbprint)
            if (certParts.Length > 3)
            {
                throw SQL.InvalidCertificatePath(keyPath, GetValidCertificateLocations(), isSystemOp);
            }

            // Extract the store location where the cert is stored
            if (certParts.Length > 2)
            {
                if (string.Equals(certParts[0], CertLocationLocalMachine, StringComparison.OrdinalIgnoreCase) == true)
                {
                    storeLocation = StoreLocation.LocalMachine;
                }
                else if (string.Equals(certParts[0], CertLocationCurrentUser, StringComparison.OrdinalIgnoreCase) == true)
                {
                    storeLocation = StoreLocation.CurrentUser;
                }
                else
                {
                    // throw an invalid certificate location exception
                    throw SQL.InvalidCertificateLocation(certParts[0], keyPath, GetValidCertificateLocations(), isSystemOp);
                }
            }

            // Parse the certificate store name
            if (certParts.Length > 1)
            {
                if (string.Equals(certParts[certParts.Length - 2], MyCertificateStore, StringComparison.OrdinalIgnoreCase) == true)
                {
                    storeName = StoreName.My;
                }
                else
                {
                    // We only support storing them in My certificate store
                    throw SQL.InvalidCertificateStore(certParts[certParts.Length - 2], keyPath, MyCertificateStore, isSystemOp);
                }
            }

            // Get thumpbrint
            string thumbprint = certParts[certParts.Length - 1];
            if (string.IsNullOrEmpty(thumbprint))
            {
                // An empty thumbprint specified
                throw SQL.EmptyCertificateThumbprint(keyPath, isSystemOp);
            }

            // Find the certificate and return
            return GetCertificate(storeLocation, storeName, keyPath, thumbprint, isSystemOp);
        }

        /// <summary>
        /// Searches for a certificate in certificate store and returns the matching certificate
        /// </summary>
        /// <param name="storeLocation">Store Location: This can be one of LocalMachine or UserName</param>
        /// <param name="storeName">Store Location: Currently this can only be My store.</param>
        /// <param name="masterKeyPath"></param>
        /// <param name="thumbprint">Certificate thumbprint</param>
        /// <param name="isSystemOp"></param>
        /// <returns>Matching certificate</returns>
        private X509Certificate2 GetCertificate(StoreLocation storeLocation, StoreName storeName, string masterKeyPath, string thumbprint, bool isSystemOp)
        {
            // Open specified certificate store
            X509Store certificateStore = null;

            try
            {
                certificateStore = new X509Store(storeName, storeLocation);
                certificateStore.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                // Search for the specified certificate
                X509Certificate2Collection matchingCertificates =
                            certificateStore.Certificates.Find(X509FindType.FindByThumbprint,
                            thumbprint,
                            false);

                // Throw an exception if a cert with the specified thumbprint is not found
                if (matchingCertificates == null || matchingCertificates.Count == 0)
                {
                    throw SQL.CertificateNotFound(thumbprint, storeName.ToString(), storeLocation.ToString(), isSystemOp);
                }

                X509Certificate2 certificate = matchingCertificates[0];
                if (!certificate.HasPrivateKey)
                {
                    // ensure the certificate has private key
                    throw SQL.CertificateWithNoPrivateKey(masterKeyPath, isSystemOp);
                }

                // return the matching certificate
                return certificate;
            }
            finally
            {
                // Close the certificate store
                certificateStore?.Close();
            }
        }

        /// <summary>
        /// Encrypt the text using specified certificate.
        /// </summary>
        /// <param name="plainText">Text to encrypt.</param>
        /// <param name="certificate">Certificate object.</param>
        /// <returns>Returns an encrypted blob or throws an exception if there are any errors.</returns>
        private byte[] RSAEncrypt(byte[] plainText, X509Certificate2 certificate)
        {
            Debug.Assert(plainText != null);
            Debug.Assert(certificate != null);
            Debug.Assert(certificate.HasPrivateKey, "Attempting to encrypt with cert without privatekey");

            RSA rsa = certificate.GetRSAPublicKey();
            
            // CodeQL [SM03796] Required for an external standard: Always Encrypted only supports encrypting column encryption keys with RSA_OAEP(SHA1) (https://learn.microsoft.com/en-us/sql/t-sql/statements/create-column-encryption-key-transact-sql?view=sql-server-ver16)
            return rsa.Encrypt(plainText, RSAEncryptionPadding.OaepSHA1);
        }

        /// <summary>
        /// Decrypt the data using specified certificate.
        /// </summary>
        /// <param name="cipherText">Text to decrypt.</param>
        /// <param name="certificate">Certificate object.</param>
        /// <returns>Returns a decrypted blob or throws an exception if there are any errors.</returns>
        private byte[] RSADecrypt(byte[] cipherText, X509Certificate2 certificate)
        {
            Debug.Assert((cipherText != null) && (cipherText.Length != 0));
            Debug.Assert(certificate != null);
            Debug.Assert(certificate.HasPrivateKey, "Attempting to decrypt with cert without privatekey");

            RSA rsa = certificate.GetRSAPrivateKey();
            
            // CodeQL [SM03796] Required for an external standard: Always Encrypted only supports encrypting column encryption keys with RSA_OAEP(SHA1) (https://learn.microsoft.com/en-us/sql/t-sql/statements/create-column-encryption-key-transact-sql?view=sql-server-ver16)
            return rsa.Decrypt(cipherText, RSAEncryptionPadding.OaepSHA1);
        }

        /// <summary>
        /// Generates signature based on RSA PKCS#v1.5 scheme using a specified certificate. 
        /// </summary>
        /// <param name="dataToSign">Text to sign.</param>
        /// <param name="certificate">Certificate object.</param>
        /// <returns>Signature</returns>
        private byte[] RSASignHashedData(byte[] dataToSign, X509Certificate2 certificate)
        {
            Debug.Assert((dataToSign != null) && (dataToSign.Length != 0));
            Debug.Assert(certificate != null);
            Debug.Assert(certificate.HasPrivateKey, "Attempting to sign with cert without privatekey");

            RSA rsa = certificate.GetRSAPrivateKey();

            // Prepare RSAPKCS1SignatureFormatter for signing the passed in hash
            RSAPKCS1SignatureFormatter rsaFormatter = new RSAPKCS1SignatureFormatter(rsa);
            //Set the hash algorithm to SHA256.
            rsaFormatter.SetHashAlgorithm(HashingAlgorithm);

            //Create a signature for HashValue and return it. 
            return rsaFormatter.CreateSignature(dataToSign);
        }

        /// <summary>
        /// Verifies the given RSA PKCSv1.5 signature.
        /// </summary>
        /// <param name="dataToVerify"></param>
        /// <param name="signature"></param>
        /// <param name="certificate"></param>
        /// <returns>true if signature is valid, false if it is not valid</returns>
        private bool RSAVerifySignature(byte[] dataToVerify, byte[] signature, X509Certificate2 certificate)
        {
            Debug.Assert((dataToVerify != null) && (dataToVerify.Length != 0));
            Debug.Assert((signature != null) && (signature.Length != 0));
            Debug.Assert(certificate != null);
            Debug.Assert(certificate.HasPrivateKey, "Attempting to sign with cert without privatekey");

            RSA rsa = certificate.GetRSAPrivateKey();

            // Prepare RSAPKCS1SignatureFormatter for signing the passed in hash
            RSAPKCS1SignatureDeformatter rsaDeFormatter = new RSAPKCS1SignatureDeformatter(rsa);

            //Set the hash algorithm to SHA256.
            rsaDeFormatter.SetHashAlgorithm(HashingAlgorithm);

            //Create a signature for HashValue and return it. 
            return rsaDeFormatter.VerifySignature(dataToVerify, signature);
        }
    }
}
