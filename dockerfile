# Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0

WORKDIR /app

# Instalar LibreOffice y dependencias
RUN apt-get update && \
    apt-get install -y libreoffice libreoffice-writer && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copiar archivos del proyecto primero (mejor para cache de Docker)
COPY ["Inmobiscrap.csproj", "./"]

# Restaurar dependencias
RUN dotnet restore "Inmobiscrap.csproj"

# Instalar dotnet-ef con versión específica compatible con .NET 9
# Opción 1: Versión específica que funciona con .NET 9
RUN dotnet tool install --global dotnet-ef --version 9.0.0

# Alternativamente, si la anterior falla, descomenta esta línea:
# RUN dotnet tool install --global dotnet-ef --version 8.0.11

ENV PATH="${PATH}:/root/.dotnet/tools"

# Copiar el resto del código
COPY . .

# Verificar que EF Tools está instalado (opcional)
RUN dotnet ef --version || echo "EF Tools verification"

# Exponer puerto
EXPOSE 8080

# Comando por defecto (se puede sobrescribir en docker-compose)
CMD ["dotnet", "run", "--urls", "http://0.0.0.0:8080"]