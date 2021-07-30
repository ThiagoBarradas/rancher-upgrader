docker build -t thiagobarradas/rancher-upgrader:latest .
docker tag thiagobarradas/rancher-upgrader:latest thiagobarradas/rancher-upgrader:latest
docker push thiagobarradas/rancher-upgrader:latest

docker tag thiagobarradas/rancher-upgrader:latest thiagobarradas/rancher-upgrader:rc