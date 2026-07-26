const crypto = require('crypto');
const http = require('http');

const port = Number(process.env.PORT || 8080);
const signingKey = process.env.LOCAL_AUTH_SIGNING_KEY || '';

const targets = {
  cadastro: new URL(process.env.CADASTRO_BASE_URL || 'http://cadastro-api:8080'),
  estoque: new URL(process.env.ESTOQUE_BASE_URL || 'http://estoque-api:8080'),
  ordens: new URL(process.env.ORDENS_BASE_URL || 'http://ordens-api:8080'),
};

const cadastroPrefixes = [
  '/api/auth/cpf',
  '/api/admin/funcionarios',
  '/api/clientes',
  '/api/veiculos',
  '/api/servicos',
];

const estoquePrefixes = [
  '/api/pecas',
  '/api/insumos',
  '/api/estoque',
];

const ordensPrefixes = [
  '/api/ordens-servico',
  '/api/orcamentos',
  '/api/meus-orcamentos',
  '/api/minhas-ordens-servico',
  '/api/relatorios',
  '/api/webhooks/payments',
];

function route(pathname) {
  if (pathname === '/health') {
    return { kind: 'self' };
  }

  if (pathname === '/health/cadastro') {
    return { target: targets.cadastro, path: '/health' };
  }

  if (pathname === '/health/estoque') {
    return { target: targets.estoque, path: '/health' };
  }

  if (pathname === '/health/ordens') {
    return { target: targets.ordens, path: '/health' };
  }

  if (pathname === '/ready/cadastro') {
    return { target: targets.cadastro, path: '/ready' };
  }

  if (pathname === '/ready/estoque') {
    return { target: targets.estoque, path: '/ready' };
  }

  if (pathname === '/ready/ordens') {
    return { target: targets.ordens, path: '/ready' };
  }

  if (cadastroPrefixes.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))) {
    return { target: targets.cadastro, path: pathname };
  }

  if (estoquePrefixes.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))) {
    return { target: targets.estoque, path: pathname };
  }

  if (ordensPrefixes.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))) {
    return { target: targets.ordens, path: pathname };
  }

  return null;
}

function authenticate(headers) {
  const authorization = headers.authorization;
  if (!authorization) {
    return null;
  }

  const match = /^Bearer\s+(.+)$/i.exec(authorization);
  if (!match) {
    throw new Error('invalid_authorization_header');
  }

  return verifyJwt(match[1]);
}

function verifyJwt(token) {
  const parts = token.split('.');
  if (parts.length !== 3 || !signingKey) {
    throw new Error('invalid_token');
  }

  const signed = `${parts[0]}.${parts[1]}`;
  const expected = hmacBase64Url(signed, signingKey);
  const actual = parts[2];
  const expectedBytes = Buffer.from(expected);
  const actualBytes = Buffer.from(actual);
  if (expectedBytes.length !== actualBytes.length || !crypto.timingSafeEqual(expectedBytes, actualBytes)) {
    throw new Error('invalid_token_signature');
  }

  const payload = JSON.parse(Buffer.from(toBase64(parts[1]), 'base64').toString('utf8'));
  const now = Math.floor(Date.now() / 1000);
  if (!payload.exp || Number(payload.exp) < now) {
    throw new Error('expired_token');
  }

  for (const claim of ['sub', 'cpf', 'role', 'name']) {
    if (!payload[claim]) {
      throw new Error(`missing_${claim}`);
    }
  }

  return payload;
}

function hmacBase64Url(value, key) {
  return crypto.createHmac('sha256', key).update(value).digest('base64url');
}

function toBase64(value) {
  const padded = value + '='.repeat((4 - (value.length % 4)) % 4);
  return padded.replace(/-/g, '+').replace(/_/g, '/');
}

function copyHeaders(headers, identity) {
  const next = { ...headers };
  for (const name of ['connection', 'host', 'keep-alive', 'proxy-authenticate', 'proxy-authorization', 'te', 'trailer', 'transfer-encoding', 'upgrade']) {
    delete next[name];
  }

  if (identity) {
    next['x-dev-cpf'] = identity.cpf;
    next['x-dev-role'] = identity.role;
    next['x-oficina-user-id'] = identity.sub;
    next['x-oficina-user-cpf'] = identity.cpf;
    next['x-oficina-user-role'] = identity.role;
    next['x-oficina-user-name'] = identity.name;

    if (String(identity.role).toLowerCase() === 'cliente') {
      next['x-dev-clienteid'] = identity.sub;
      delete next['x-dev-funcionarioid'];
    } else {
      next['x-dev-funcionarioid'] = identity.sub;
      delete next['x-dev-clienteid'];
    }
  }

  return next;
}

function proxy(req, res, selected, parsedUrl, identity) {
  const target = selected.target;
  const requestPath = `${selected.path}${parsedUrl.search}`;
  const options = {
    protocol: target.protocol,
    hostname: target.hostname,
    port: target.port,
    method: req.method,
    path: requestPath,
    headers: copyHeaders(req.headers, identity),
  };

  const upstream = http.request(options, (upstreamResponse) => {
    res.writeHead(upstreamResponse.statusCode || 502, upstreamResponse.headers);
    upstreamResponse.pipe(res);
  });

  upstream.on('error', () => {
    if (!res.headersSent) {
      res.writeHead(502, { 'content-type': 'application/json' });
    }
    res.end(JSON.stringify({ error: 'local_gateway_upstream_unavailable' }));
  });

  req.pipe(upstream);
}

const server = http.createServer((req, res) => {
  const parsedUrl = new URL(req.url || '/', `http://${req.headers.host || 'local-gateway'}`);
  const selected = route(parsedUrl.pathname);

  if (!selected) {
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: 'local_gateway_route_not_found' }));
    return;
  }

  if (selected.kind === 'self') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ status: 'Healthy', service: 'oficina-local-gateway' }));
    return;
  }

  let identity = null;
  try {
    identity = authenticate(req.headers);
  } catch {
    res.writeHead(401, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: 'local_gateway_invalid_token' }));
    return;
  }

  proxy(req, res, selected, parsedUrl, identity);
});

server.listen(port, '0.0.0.0', () => {
  console.log(`oficina-local-gateway listening on ${port}`);
});
