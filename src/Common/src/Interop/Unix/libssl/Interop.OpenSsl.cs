// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static class OpenSsl
    {
        private static Ssl.SslCtxSetVerifyCallback s_verifyClientCertificate = VerifyClientCertificate;

        #region internal methods

        internal static SafeChannelBindingHandle QueryChannelBinding(SafeSslHandle context, ChannelBindingKind bindingType)
        {
            SafeChannelBindingHandle bindingHandle;
            switch (bindingType)
            {
                case ChannelBindingKind.Endpoint:
                    bindingHandle = new SafeChannelBindingHandle(bindingType);
                    QueryEndPointChannelBinding(context, bindingHandle);
                    break;

                case ChannelBindingKind.Unique:
                    bindingHandle = new SafeChannelBindingHandle(bindingType);
                    QueryUniqueChannelBinding(context, bindingHandle);
                    break;

                default:
                    // Keeping parity with windows, we should return null in this case.
                    bindingHandle = null;
                    break;
            }

            return bindingHandle;
        }

        internal static SafeSslHandle AllocateSslContext(SslProtocols protocols, SafeX509Handle certHandle, SafeEvpPKeyHandle certKeyHandle, EncryptionPolicy policy, bool isServer, bool remoteCertRequired)
        {
            SafeSslHandle context = null;

            IntPtr method = GetSslMethod(protocols);

            using (SafeSslContextHandle innerContext = Ssl.SslCtxCreate(method))
            {
                if (innerContext.IsInvalid)
                {
                    throw CreateSslException(SR.net_allocate_ssl_context_failed);
                }

                Ssl.SetProtocolOptions(innerContext, protocols);

                Ssl.SslCtxSetQuietShutdown(innerContext);

                Ssl.SetEncryptionPolicy(innerContext, policy);

                if (certHandle != null && certKeyHandle != null)
                {
                    SetSslCertificate(innerContext, certHandle, certKeyHandle);
                }

                if (remoteCertRequired)
                {
                    Debug.Assert(isServer, "isServer flag should be true");
                    Ssl.SslCtxSetVerify(innerContext,
                        s_verifyClientCertificate);

                    //update the client CA list 
                    UpdateCAListFromRootStore(innerContext);
                }

                context = SafeSslHandle.Create(innerContext, isServer);
                Debug.Assert(context != null, "Expected non-null return value from SafeSslHandle.Create");
                if (context.IsInvalid)
                {
                    context.Dispose();
                    throw CreateSslException(SR.net_allocate_ssl_context_failed);
                }
            }

            return context;
        }

        internal static bool DoSslHandshake(SafeSslHandle context, byte[] recvBuf, int recvOffset, int recvCount, out byte[] sendBuf, out int sendCount)
        {
            sendBuf = null;
            sendCount = 0;
            if ((recvBuf != null) && (recvCount > 0))
            {
                BioWrite(context.InputBio, recvBuf, recvOffset, recvCount);
            }

            Ssl.SslErrorCode error;
            int retVal = Ssl.SslDoHandshake(context);

            if (retVal != 1)
            {
                error = GetSslError(context, retVal);

                if ((retVal != -1) || (error != Ssl.SslErrorCode.SSL_ERROR_WANT_READ))
                {
                    throw CreateSslException(context, SR.net_ssl_handshake_failed_error, retVal);
                }
            }

            sendCount = Crypto.BioCtrlPending(context.OutputBio);

            if (sendCount > 0)
            {
                sendBuf = new byte[sendCount];

                try
                {
                    sendCount = BioRead(context.OutputBio, sendBuf, sendCount);
                }
                finally
                {
                    if (sendCount <= 0)
                    {
                        sendBuf = null;
                        sendCount = 0;
                    }
                }
            }

            return Ssl.IsSslStateOK(context);
        }

        internal static int Encrypt(SafeSslHandle context, byte[] buffer, int offset, int count, out Ssl.SslErrorCode errorCode)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(count >= 0);
            Debug.Assert(buffer.Length >= offset + count);

            errorCode = Ssl.SslErrorCode.SSL_ERROR_NONE;

            int retVal;
            unsafe
            {
                fixed (byte* fixedBuffer = buffer)
                {
                    retVal = Ssl.SslWrite(context, fixedBuffer + offset, count);
                }
            }

            if (retVal != count)
            {
                errorCode = GetSslError(context, retVal);
                retVal = 0;

                switch (errorCode)
                {
                    // indicate end-of-file
                    case Ssl.SslErrorCode.SSL_ERROR_ZERO_RETURN:
                    case Ssl.SslErrorCode.SSL_ERROR_WANT_READ:
                        break;

                    default:
                        throw CreateSslException(SR.net_ssl_encrypt_failed, errorCode);
                }
            }
            else
            {
                int capacityNeeded = Crypto.BioCtrlPending(context.OutputBio);

                Debug.Assert(buffer.Length >= capacityNeeded, "Input buffer of size " + buffer.Length +
                                                              " bytes is insufficient since encryption needs " + capacityNeeded + " bytes.");

                retVal = BioRead(context.OutputBio, buffer, capacityNeeded);
            }

            return retVal;
        }

        internal static int Decrypt(SafeSslHandle context, byte[] outBuffer, int count, out Ssl.SslErrorCode errorCode)
        {
            errorCode = Ssl.SslErrorCode.SSL_ERROR_NONE;

            int retVal = BioWrite(context.InputBio, outBuffer, 0, count);

            if (retVal == count)
            {
                retVal = Ssl.SslRead(context, outBuffer, retVal);

                if (retVal > 0)
                {
                    count = retVal;
                }
            }

            if (retVal != count)
            {
                errorCode = GetSslError(context, retVal);
                retVal = 0;

                switch (errorCode)
                {
                    // indicate end-of-file
                    case Ssl.SslErrorCode.SSL_ERROR_ZERO_RETURN:
                        break;

                    case Ssl.SslErrorCode.SSL_ERROR_WANT_READ:
                        // update error code to renegotiate if renegotiate is pending, otherwise make it SSL_ERROR_WANT_READ
                        errorCode = Ssl.IsSslRenegotiatePending(context) ?
                                    Ssl.SslErrorCode.SSL_ERROR_RENEGOTIATE :
                                    Ssl.SslErrorCode.SSL_ERROR_WANT_READ;
                        break;

                    default:
                        throw CreateSslException(SR.net_ssl_decrypt_failed, errorCode);
                }
            }

            return retVal;
        }

        internal static SafeX509Handle GetPeerCertificate(SafeSslHandle context)
        {
            return Ssl.SslGetPeerCertificate(context);
        }

        internal static SafeSharedX509StackHandle GetPeerCertificateChain(SafeSslHandle context)
        {
            return Ssl.SslGetPeerCertChain(context);
        }

        internal static void FreeSslContext(SafeSslHandle context)
        {
            Debug.Assert((context != null) && !context.IsInvalid, "Expected a valid context in FreeSslContext");
            Disconnect(context);
            context.Dispose();
        }

        #endregion

        #region private methods

        private static void QueryEndPointChannelBinding(SafeSslHandle context, SafeChannelBindingHandle bindingHandle)
        {
            using (SafeX509Handle certSafeHandle = GetPeerCertificate(context))
            {
                if (certSafeHandle == null || certSafeHandle.IsInvalid)
                {
                    throw CreateSslException(SR.net_ssl_invalid_certificate);
                }

                bool gotReference = false;

                try
                {
                    certSafeHandle.DangerousAddRef(ref gotReference);
                    using (X509Certificate2 cert = new X509Certificate2(certSafeHandle.DangerousGetHandle()))
                    using (HashAlgorithm hashAlgo = GetHashForChannelBinding(cert))
                    {
                        byte[] bindingHash = hashAlgo.ComputeHash(cert.RawData);
                        bindingHandle.SetCertHash(bindingHash);
                    }
                }
                finally
                {
                    if (gotReference)
                    {
                        certSafeHandle.DangerousRelease();
                    }
                }
            }
        }

        private static void QueryUniqueChannelBinding(SafeSslHandle context, SafeChannelBindingHandle bindingHandle)
        {
            bool sessionReused = Ssl.SslSessionReused(context);
            int certHashLength = context.IsServer ^ sessionReused ?
                                 Ssl.SslGetPeerFinished(context, bindingHandle.CertHashPtr, bindingHandle.Length) :
                                 Ssl.SslGetFinished(context, bindingHandle.CertHashPtr, bindingHandle.Length);

            if (0 == certHashLength)
            {
                throw CreateSslException(SR.net_ssl_get_channel_binding_token_failed);
            }

            bindingHandle.SetCertHashLength(certHashLength);
        }

        private static HashAlgorithm GetHashForChannelBinding(X509Certificate2 cert)
        {
            Oid signatureAlgorithm = cert.SignatureAlgorithm;
            switch (signatureAlgorithm.Value)
            {
                // RFC 5929 4.1 says that MD5 and SHA1 both upgrade to EvpSha256 for cbt calculation
                case "1.2.840.113549.2.5": // MD5
                case "1.2.840.113549.1.1.4": // MD5RSA
                case "1.3.14.3.2.26": // SHA1
                case "1.2.840.10040.4.3": // SHA1DSA
                case "1.2.840.10045.4.1": // SHA1ECDSA
                case "1.2.840.113549.1.1.5": // SHA1RSA
                case "2.16.840.1.101.3.4.2.1": // SHA256
                case "1.2.840.10045.4.3.2": // SHA256ECDSA
                case "1.2.840.113549.1.1.11": // SHA256RSA
                    return SHA256.Create();

                case "2.16.840.1.101.3.4.2.2": // SHA384
                case "1.2.840.10045.4.3.3": // SHA384ECDSA
                case "1.2.840.113549.1.1.12": // SHA384RSA
                    return SHA384.Create();

                case "2.16.840.1.101.3.4.2.3": // SHA512
                case "1.2.840.10045.4.3.4": // SHA512ECDSA
                case "1.2.840.113549.1.1.13": // SHA512RSA
                    return SHA512.Create();

                default:
                    throw new ArgumentException(signatureAlgorithm.Value);
            }
        }

        private static IntPtr GetSslMethod(SslProtocols protocols)
        {
            Debug.Assert(protocols != SslProtocols.None, "All protocols are disabled");

            bool ssl2 = (protocols & SslProtocols.Ssl2) == SslProtocols.Ssl2;
            bool ssl3 = (protocols & SslProtocols.Ssl3) == SslProtocols.Ssl3;
            bool tls10 = (protocols & SslProtocols.Tls) == SslProtocols.Tls;
            bool tls11 = (protocols & SslProtocols.Tls11) == SslProtocols.Tls11;
            bool tls12 = (protocols & SslProtocols.Tls12) == SslProtocols.Tls12;

            IntPtr method = Ssl.SslMethods.SSLv23_method; // default
            string methodName = "SSLv23_method";

            if (!ssl2)
            {
                if (!ssl3)
                {
                    if (!tls11 && !tls12)
                    {
                        method = Ssl.SslMethods.TLSv1_method;
                        methodName = "TLSv1_method";
                    }
                    else if (!tls10 && !tls12)
                    {
                        method = Ssl.SslMethods.TLSv1_1_method;
                        methodName = "TLSv1_1_method";
                    }
                    else if (!tls10 && !tls11)
                    {
                        method = Ssl.SslMethods.TLSv1_2_method;
                        methodName = "TLSv1_2_method";
                    }
                }
                else if (!tls10 && !tls11 && !tls12)
                {
                    method = Ssl.SslMethods.SSLv3_method;
                    methodName = "SSLv3_method";
                }
            }

            if (IntPtr.Zero == method)
            {
                throw new SslException(SR.Format(SR.net_get_ssl_method_failed, methodName));
            }

            return method;
        }

        private static int VerifyClientCertificate(int preverify_ok, IntPtr x509_ctx_ptr)
        {
            using (SafeX509StoreCtxHandle storeHandle = new SafeX509StoreCtxHandle(x509_ctx_ptr, false))
            {
                using (var chain = new X509Chain())
                {
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;

                    using (SafeX509StackHandle chainStack = Crypto.X509StoreCtxGetChain(storeHandle))
                    {
                        if (chainStack.IsInvalid)
                        {
                            Debug.Fail("Invalid chain stack handle");
                            return 0;
                        }

                        IntPtr certPtr = Crypto.GetX509StackField(chainStack, 0);
                        if (IntPtr.Zero == certPtr)
                        {
                            return 0;
                        }

                        using (X509Certificate2 cert = new X509Certificate2(certPtr))
                        {
                            return chain.Build(cert) ? 1 : 0;
                        }
                    }
                }
            }
        }

        private static void UpdateCAListFromRootStore(SafeSslContextHandle context)
        {
            using (SafeX509NameStackHandle nameStack = Crypto.NewX509NameStack())
            {
                //maintaining the HashSet of Certificate's issuer name to keep track of duplicates 
                HashSet<string> issuerNameHashSet = new HashSet<string>();

                //Enumerate Certificates from LocalMachine and CurrentUser root store 
                AddX509Names(nameStack, StoreLocation.LocalMachine, issuerNameHashSet);
                AddX509Names(nameStack, StoreLocation.CurrentUser, issuerNameHashSet);

                Ssl.SslCtxSetClientCAList(context, nameStack);

                // The handle ownership has been transferred into the CTX.
                nameStack.SetHandleAsInvalid();
            }

        }

        private static void AddX509Names(SafeX509NameStackHandle nameStack, StoreLocation storeLocation, HashSet<string> issuerNameHashSet)
        {
            using (var store = new X509Store(StoreName.Root, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);

                foreach (var certificate in store.Certificates)
                {
                    //Check if issuer name is already present
                    //Avoiding duplicate names
                    if (!issuerNameHashSet.Add(certificate.Issuer))
                    {
                        continue;
                    }

                    using (SafeX509Handle certHandle = Crypto.X509Duplicate(certificate.Handle))
                    {
                        using (SafeX509NameHandle nameHandle = Crypto.DuplicateX509Name(Crypto.X509GetIssuerName(certHandle)))
                        {
                            if (Crypto.PushX509NameStackField(nameStack, nameHandle))
                            {
                                // The handle ownership has been transferred into the STACK_OF(X509_NAME).
                                nameHandle.SetHandleAsInvalid();
                            }
                            else
                            {
                                throw new CryptographicException(SR.net_ssl_x509Name_push_failed_error);
                            }
                        }
                    }
                }
            }
        }

        private static void Disconnect(SafeSslHandle context)
        {
            int retVal = Ssl.SslShutdown(context);
            if (retVal < 0)
            {
                //TODO (Issue #4031) check this error
                Ssl.SslGetError(context, retVal);
            }
        }

        //TODO (Issue #3362) should we check Bio should retry?
        private static int BioRead(SafeBioHandle bio, byte[] buffer, int count)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(count >= 0);
            Debug.Assert(buffer.Length >= count);

            int bytes = Crypto.BioRead(bio, buffer, count);
            if (bytes != count)
            {
                throw CreateSslException(SR.net_ssl_read_bio_failed_error);
            }
            return bytes;
        }

        //TODO (Issue #3362) should we check Bio should retry?
        private static int BioWrite(SafeBioHandle bio, byte[] buffer, int offset, int count)
        {
            Debug.Assert(buffer != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(count >= 0);
            Debug.Assert(buffer.Length >= offset + count);

            int bytes;
            unsafe
            {
                fixed (byte* bufPtr = buffer)
                {
                    bytes = Ssl.BioWrite(bio, bufPtr + offset, count);
                }
            }

            if (bytes != count)
            {
                throw CreateSslException(SR.net_ssl_write_bio_failed_error);
            }
            return bytes;
        }

        private static Ssl.SslErrorCode GetSslError(SafeSslHandle context, int result)
        {
            Ssl.SslErrorCode retVal = Ssl.SslGetError(context, result);
            if (retVal == Ssl.SslErrorCode.SSL_ERROR_SYSCALL)
            {
                retVal = (Ssl.SslErrorCode)Crypto.ErrGetError();
            }
            return retVal;
        }

        private static void SetSslCertificate(SafeSslContextHandle contextPtr, SafeX509Handle certPtr, SafeEvpPKeyHandle keyPtr)
        {
            Debug.Assert(certPtr != null && !certPtr.IsInvalid, "certPtr != null && !certPtr.IsInvalid");
            Debug.Assert(keyPtr != null && !keyPtr.IsInvalid, "keyPtr != null && !keyPtr.IsInvalid");

            int retVal = Ssl.SslCtxUseCertificate(contextPtr, certPtr);

            if (1 != retVal)
            {
                throw CreateSslException(SR.net_ssl_use_cert_failed);
            }

            retVal = Ssl.SslCtxUsePrivateKey(contextPtr, keyPtr);

            if (1 != retVal)
            {
                throw CreateSslException(SR.net_ssl_use_private_key_failed);
            }

            //check private key
            retVal = Ssl.SslCtxCheckPrivateKey(contextPtr);

            if (1 != retVal)
            {
                throw CreateSslException(SR.net_ssl_check_private_key_failed);
            }
        }

        internal static SslException CreateSslException(string message)
        {
            ulong errorVal = Crypto.ErrGetError();
            string msg = SR.Format(message, Marshal.PtrToStringAnsi(Crypto.ErrReasonErrorString(errorVal)));
            return new SslException(msg, (int)errorVal);
        }

        private static SslException CreateSslException(string message, Ssl.SslErrorCode error)
        {
            string msg = SR.Format(message, error);
            switch (error)
            {
                case Ssl.SslErrorCode.SSL_ERROR_SYSCALL:
                    return CreateSslException(msg);

                case Ssl.SslErrorCode.SSL_ERROR_SSL:
                    Exception innerEx = Interop.Crypto.CreateOpenSslCryptographicException();
                    return new SslException(innerEx.Message, innerEx);

                default:
                    return new SslException(msg, error);
            }
        }

        private static SslException CreateSslException(SafeSslHandle context, string message, int error)
        {
            return CreateSslException(message, Ssl.SslGetError(context, error));
        }

        #endregion

        #region Internal class

        internal sealed class SslException : Exception
        {
            public SslException(string inputMessage)
                : base(inputMessage)
            {
            }

            public SslException(string inputMessage, Exception ex)
                : base(inputMessage, ex)
            {
            }

            public SslException(string inputMessage, Ssl.SslErrorCode error)
                : this(inputMessage, (int)error)
            {
            }

            public SslException(string inputMessage, int error)
                : this(inputMessage)
            {
                HResult = error;
            }

            public SslException(int error)
                : this(SR.Format(SR.net_generic_operation_failed, error))
            {
                HResult = error;
            }
        }

        #endregion
    }
}
