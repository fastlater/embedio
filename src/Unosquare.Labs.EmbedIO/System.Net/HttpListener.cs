﻿#if !NET452
//
// System.Net.HttpListener
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@novell.com)
//	Marek Safar (marek.safar@gmail.com)
//
// Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
// Copyright 2011 Xamarin Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace System.Net
{
    public delegate AuthenticationSchemes AuthenticationSchemeSelector(HttpListenerRequest httpRequest);

    public sealed class HttpListener : IDisposable
    {
        AuthenticationSchemes auth_schemes;
        HttpListenerPrefixCollection prefixes;
        AuthenticationSchemeSelector auth_selector;
        string realm;
        bool ignore_write_exceptions;
        bool unsafe_ntlm_auth;
        bool listening;
        bool disposed;
#if SSL
        IMonoTlsProvider tlsProvider;
        MSI.MonoTlsSettings tlsSettings;
        X509Certificate certificate;
#endif

        Hashtable registry;   // Dictionary<HttpListenerContext,HttpListenerContext> 
        ArrayList ctx_queue;  // List<HttpListenerContext> ctx_queue;
        ArrayList wait_queue; // List<ListenerAsyncResult> wait_queue;
        Hashtable connections;

        //ServiceNameStore defaultServiceNames;
        ExtendedProtectionPolicy extendedProtectionPolicy;
        ExtendedProtectionSelector extendedProtectionSelectorDelegate;

        public delegate ExtendedProtectionPolicy ExtendedProtectionSelector(HttpListenerRequest request);

        public HttpListener()
        {
            prefixes = new HttpListenerPrefixCollection(this);
            registry = new Hashtable();
            connections = Hashtable.Synchronized(new Hashtable());
            ctx_queue = new ArrayList();
            wait_queue = new ArrayList();
            auth_schemes = AuthenticationSchemes.Anonymous;
            //defaultServiceNames = new ServiceNameStore();
            extendedProtectionPolicy = new ExtendedProtectionPolicy(PolicyEnforcement.Never);
        }

#if SSL
        internal HttpListener(X509Certificate certificate, IMonoTlsProvider tlsProvider, MSI.MonoTlsSettings tlsSettings)
            : this()
        {
            this.certificate = certificate;
            this.tlsProvider = tlsProvider;
            this.tlsSettings = tlsSettings;
        }

        internal X509Certificate LoadCertificateAndKey(IPAddress addr, int port)
        {
            lock (registry)
            {
                if (certificate != null)
                    return certificate;

                // Actually load the certificate
                try
                {
                    string dirname = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string path = Path.Combine(dirname, ".mono");
                    path = Path.Combine(path, "httplistener");
                    string cert_file = Path.Combine(path, String.Format("{0}.cer", port));
                    if (!File.Exists(cert_file))
                        return null;
                    string pvk_file = Path.Combine(path, String.Format("{0}.pvk", port));
                    if (!File.Exists(pvk_file))
                        return null;
                    var cert = new X509Certificate2(cert_file);
                    cert.PrivateKey = PrivateKey.CreateFromFile(pvk_file).RSA;
                    certificate = cert;
                    return certificate;
                }
                catch
                {
                    // ignore errors
                    certificate = null;
                    return null;
                }
            }
        }
        
        internal IMonoSslStream CreateSslStream(Stream innerStream, bool ownsStream, MSI.MonoRemoteCertificateValidationCallback callback)
        {
            lock (registry)
            {
                if (tlsProvider == null)
                    tlsProvider = MonoTlsProviderFactory.GetProviderInternal();
                if (tlsSettings == null)
                    tlsSettings = MSI.MonoTlsSettings.CopyDefaultSettings();
                if (tlsSettings.RemoteCertificateValidationCallback == null)
                    tlsSettings.RemoteCertificateValidationCallback = callback;
                return tlsProvider.CreateSslStream(innerStream, ownsStream, tlsSettings);
            }
        }
#endif
        // TODO: Digest, NTLM and Negotiate require ControlPrincipal
        public AuthenticationSchemes AuthenticationSchemes
        {
            get { return auth_schemes; }
            set
            {
                CheckDisposed();
                auth_schemes = value;
            }
        }

        public AuthenticationSchemeSelector AuthenticationSchemeSelectorDelegate
        {
            get { return auth_selector; }
            set
            {
                CheckDisposed();
                auth_selector = value;
            }
        }

        //public ExtendedProtectionSelector ExtendedProtectionSelectorDelegate
        //{
        //    get { return extendedProtectionSelectorDelegate; }
        //    set
        //    {
        //        CheckDisposed();
        //        if (value == null)
        //            throw new ArgumentNullException();

        //        if (!AuthenticationManager.OSSupportsExtendedProtection)
        //            throw new PlatformNotSupportedException(SR.GetString(SR.security_ExtendedProtection_NoOSSupport));

        //        extendedProtectionSelectorDelegate = value;
        //    }
        //}

        public bool IgnoreWriteExceptions
        {
            get { return ignore_write_exceptions; }
            set
            {
                CheckDisposed();
                ignore_write_exceptions = value;
            }
        }

        public bool IsListening
        {
            get { return listening; }
        }

        public static bool IsSupported
        {
            get { return true; }
        }

        public HttpListenerPrefixCollection Prefixes
        {
            get
            {
                CheckDisposed();
                return prefixes;
            }
        }

        //[MonoTODO]
        //public HttpListenerTimeoutManager TimeoutManager
        //{
        //    get
        //    {
        //        throw new NotImplementedException();
        //    }
        //}

        //[MonoTODO("not used anywhere in the implementation")]
        //public ExtendedProtectionPolicy ExtendedProtectionPolicy
        //{
        //    get
        //    {
        //        return extendedProtectionPolicy;
        //    }
        //    set
        //    {
        //        CheckDisposed();

        //        if (value == null)
        //            throw new ArgumentNullException("value");

        //        if (!AuthenticationManager.OSSupportsExtendedProtection && value.PolicyEnforcement == PolicyEnforcement.Always)
        //            throw new PlatformNotSupportedException(SR.GetString(SR.security_ExtendedProtection_NoOSSupport));

        //        if (value.CustomChannelBinding != null)
        //            throw new ArgumentException(SR.GetString(SR.net_listener_cannot_set_custom_cbt), "CustomChannelBinding");

        //        extendedProtectionPolicy = value;
        //    }
        //}

        //public ServiceNameCollection DefaultServiceNames
        //{
        //    get
        //    {
        //        return defaultServiceNames.ServiceNames;
        //    }
        //}

        // TODO: use this
        public string Realm
        {
            get { return realm; }
            set
            {
                CheckDisposed();
                realm = value;
            }
        }

        [MonoTODO("Support for NTLM needs some loving.")]
        public bool UnsafeConnectionNtlmAuthentication
        {
            get { return unsafe_ntlm_auth; }
            set
            {
                CheckDisposed();
                unsafe_ntlm_auth = value;
            }
        }

        public void Abort()
        {
            if (disposed)
                return;

            if (!listening)
            {
                return;
            }

            Close(true);
        }

        public void Close()
        {
            if (disposed)
                return;

            if (!listening)
            {
                disposed = true;
                return;
            }

            Close(true);
            disposed = true;
        }

        void Close(bool force)
        {
            CheckDisposed();
            EndPointManager.RemoveListener(this);
            Cleanup(force);
        }

        void Cleanup(bool close_existing)
        {
            lock (registry)
            {
                if (close_existing)
                {
                    // Need to copy this since closing will call UnregisterContext
                    ICollection keys = registry.Keys;
                    var all = new HttpListenerContext[keys.Count];
                    keys.CopyTo(all, 0);
                    registry.Clear();
                    for (int i = all.Length - 1; i >= 0; i--)
                        all[i].Connection.Close(true);
                }

                lock (connections.SyncRoot)
                {
                    ICollection keys = connections.Keys;
                    var conns = new HttpConnection[keys.Count];
                    keys.CopyTo(conns, 0);
                    connections.Clear();
                    for (int i = conns.Length - 1; i >= 0; i--)
                        conns[i].Close(true);
                }
                lock (ctx_queue)
                {
                    var ctxs = (HttpListenerContext[])ctx_queue.ToArray(typeof(HttpListenerContext));
                    ctx_queue.Clear();
                    for (int i = ctxs.Length - 1; i >= 0; i--)
                        ctxs[i].Connection.Close(true);
                }

                lock (wait_queue)
                {
                    Exception exc = new ObjectDisposedException("listener");
                    foreach (ListenerAsyncResult ares in wait_queue)
                    {
                        ares.Complete(exc);
                    }
                    wait_queue.Clear();
                }
            }
        }

        public IAsyncResult BeginGetContext(AsyncCallback callback, Object state)
        {
            CheckDisposed();
            if (!listening)
                throw new InvalidOperationException("Please, call Start before using this method.");

            ListenerAsyncResult ares = new ListenerAsyncResult(callback, state);

            // lock wait_queue early to avoid race conditions
            lock (wait_queue)
            {
                lock (ctx_queue)
                {
                    HttpListenerContext ctx = GetContextFromQueue();
                    if (ctx != null)
                    {
                        ares.Complete(ctx, true);
                        return ares;
                    }
                }

                wait_queue.Add(ares);
            }

            return ares;
        }

        public HttpListenerContext EndGetContext(IAsyncResult asyncResult)
        {
            if (disposed) return null;
            if (asyncResult == null)
                throw new ArgumentNullException("asyncResult");

            ListenerAsyncResult ares = asyncResult as ListenerAsyncResult;
            if (ares == null)
                throw new ArgumentException("Wrong IAsyncResult.", "asyncResult");
            if (ares.EndCalled)
                throw new ArgumentException("Cannot reuse this IAsyncResult");
            ares.EndCalled = true;

            if (!ares.IsCompleted)
                ares.AsyncWaitHandle.WaitOne();

            lock (wait_queue)
            {
                int idx = wait_queue.IndexOf(ares);
                if (idx >= 0)
                    wait_queue.RemoveAt(idx);
            }

            HttpListenerContext context = ares.GetContext();
            context.ParseAuthentication(SelectAuthenticationScheme(context));
            return context; // This will throw on error.
        }

        internal AuthenticationSchemes SelectAuthenticationScheme(HttpListenerContext context)
        {
            if (AuthenticationSchemeSelectorDelegate != null)
                return AuthenticationSchemeSelectorDelegate(context.Request);
            else
                return auth_schemes;
        }

        public HttpListenerContext GetContext()
        {
            // The prefixes are not checked when using the async interface!?
            if (prefixes.Count == 0)
                throw new InvalidOperationException("Please, call AddPrefix before using this method.");

            ListenerAsyncResult ares = (ListenerAsyncResult)BeginGetContext(null, null);
            ares.InGet = true;
            return EndGetContext(ares);
        }

        public void Start()
        {
            CheckDisposed();
            if (listening)
                return;

            EndPointManager.AddListener(this);
            listening = true;
        }

        public void Stop()
        {
            CheckDisposed();
            listening = false;
            Close(false);
        }

        void IDisposable.Dispose()
        {
            if (disposed)
                return;

            Close(true); //TODO: Should we force here or not?
            disposed = true;
        }

        public Task<HttpListenerContext> GetContextAsync()
        {
            return Task<HttpListenerContext>.Factory.FromAsync(BeginGetContext, EndGetContext, null);
        }

        internal void CheckDisposed()
        {
            //if (disposed)
            //    throw new ObjectDisposedException(GetType().ToString());
        }

        // Must be called with a lock on ctx_queue
        HttpListenerContext GetContextFromQueue()
        {
            if (ctx_queue.Count == 0)
                return null;

            HttpListenerContext context = (HttpListenerContext)ctx_queue[0];
            ctx_queue.RemoveAt(0);
            return context;
        }

        internal void RegisterContext(HttpListenerContext context)
        {
            lock (registry)
                registry[context] = context;

            ListenerAsyncResult ares = null;
            lock (wait_queue)
            {
                if (wait_queue.Count == 0)
                {
                    lock (ctx_queue)
                        ctx_queue.Add(context);
                }
                else
                {
                    ares = (ListenerAsyncResult)wait_queue[0];
                    wait_queue.RemoveAt(0);
                }
            }
            if (ares != null)
                ares.Complete(context);
        }

        internal void UnregisterContext(HttpListenerContext context)
        {
            lock (registry)
                registry.Remove(context);
            lock (ctx_queue)
            {
                int idx = ctx_queue.IndexOf(context);
                if (idx >= 0)
                    ctx_queue.RemoveAt(idx);
            }
        }

        internal void AddConnection(HttpConnection cnc)
        {
            connections[cnc] = cnc;
        }

        internal void RemoveConnection(HttpConnection cnc)
        {
            connections.Remove(cnc);
        }
    }
}
#endif