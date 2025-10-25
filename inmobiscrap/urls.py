from django.urls import path, include
from rest_framework.routers import DefaultRouter
from scraping.api.views import (
    URLToScrapeViewSet, CasaViewSet, DepartamentoViewSet,
    TerrenoViewSet, CasaPrefabricadaViewSet, ScrapingLogViewSet,
    DashboardViewSet
)

from scraping.api.auth_views import (
    get_csrf_token, login_view, logout_view, check_auth )

router = DefaultRouter()
router.register(r'urls', URLToScrapeViewSet, basename='url-to-scrape')
router.register(r'casas', CasaViewSet, basename='casa')
router.register(r'departamentos', DepartamentoViewSet, basename='departamento')
router.register(r'terrenos', TerrenoViewSet, basename='terreno')
router.register(r'casas-prefabricadas', CasaPrefabricadaViewSet, basename='casa-prefabricada')
router.register(r'logs', ScrapingLogViewSet, basename='scraping-log')
router.register(r'dashboard', DashboardViewSet, basename='dashboard')

urlpatterns = [
    # RUTAS DE AUTENTICACIÓN - Mapeamos URL -> Vista
    path('auth/csrf/', get_csrf_token, name='csrf-token'),      # /api/v1/auth/csrf/
    path('auth/login/', login_view, name='login'),               # /api/v1/auth/login/
    path('auth/logout/', logout_view, name='logout'),            # /api/v1/auth/logout/
    path('auth/check/', check_auth, name='check-auth'),          # /api/v1/auth/check/
    
    # RUTAS DEL ROUTER (ViewSets)
    path('', include(router.urls)),  # Incluye todas las rutas del router
]