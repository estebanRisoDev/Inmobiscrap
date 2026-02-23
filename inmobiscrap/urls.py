from django.contrib import admin
from django.urls import path, include
from rest_framework.routers import DefaultRouter
from inmobiscrap.scraping.api.views import (
    URLToScrapeViewSet, CasaViewSet, DepartamentoViewSet,
    TerrenoViewSet, CasaPrefabricadaViewSet, ScrapingLogViewSet,
    DashboardViewSet
)
from inmobiscrap.scraping.api.auth_views import (
    get_csrf_token, login_view, logout_view, check_auth
)

# Router para los ViewSets
router = DefaultRouter()
router.register(r'urls', URLToScrapeViewSet, basename='url-to-scrape')
router.register(r'casas', CasaViewSet, basename='casa')
router.register(r'departamentos', DepartamentoViewSet, basename='departamento')
router.register(r'terrenos', TerrenoViewSet, basename='terreno')
router.register(r'casas-prefabricadas', CasaPrefabricadaViewSet, basename='casa-prefabricada')
router.register(r'logs', ScrapingLogViewSet, basename='scraping-log')
router.register(r'dashboard', DashboardViewSet, basename='dashboard')

# URLs de la API v1
api_v1_patterns = [
    # Autenticación
    path('auth/csrf/', get_csrf_token, name='csrf-token'),
    path('auth/login/', login_view, name='login'),
    path('auth/logout/', logout_view, name='logout'),
    path('auth/check/', check_auth, name='check-auth'),
    
    # Router (ViewSets)
    path('', include(router.urls)),
]

# URLs principales del proyecto
urlpatterns = [
    path('admin/', admin.site.urls),
    path('api/v1/', include(api_v1_patterns)),  # ← Aquí está el prefijo /api/v1/
]