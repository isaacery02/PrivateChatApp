# Linux Alpine-based nginx image
FROM nginx:1.27-alpine

# Remove default config
RUN rm /etc/nginx/conf.d/default.conf

# Copy custom nginx config
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Copy static frontend files
COPY index.html    /usr/share/nginx/html/
COPY admin.html    /usr/share/nginx/html/
COPY app.js        /usr/share/nginx/html/
COPY styles.css    /usr/share/nginx/html/
COPY manifest.json /usr/share/nginx/html/
COPY sw.js         /usr/share/nginx/html/
COPY icon.svg      /usr/share/nginx/html/

EXPOSE 80
