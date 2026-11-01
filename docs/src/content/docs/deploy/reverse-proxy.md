---
title: Behind a reverse proxy
description: Terminate TLS at Caddy or nginx and forward WebSockets to fuseraft-web.
---

Terminate TLS at the proxy and forward to `127.0.0.1:5000`. The proxy must pass WebSockets, because every page runs over a Blazor Server connection. `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (above) makes fuseraft-web trust the proxy's `X-Forwarded-Proto` and `X-Forwarded-For`, so sign-in cookies are marked secure and rate limiting sees real client addresses. Only set it when a proxy sits in front, because clients could otherwise forge those headers.

Caddy:

```
fuseraft.example.com {
    reverse_proxy 127.0.0.1:5000
}
```

nginx:

```nginx
server {
    listen 443 ssl;
    server_name fuseraft.example.com;
    # ssl_certificate …; ssl_certificate_key …;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 1h;   # long-lived Blazor and approval connections
    }
}
```

fuseraft-web logs a warning at startup if it listens on plain HTTP on anything other than loopback.
