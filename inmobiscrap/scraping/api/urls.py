from django.urls import path, include
from rest_framework.routers import DefaultRouter
from scraping.api.views import (
    URLToScrapeViewSet, CasaViewSet, DepartamentoViewSet,
    TerrenoViewSet, CasaPrefabricadaViewSet, ScrapingLogViewSet,
    DashboardViewSet
)

router = DefaultRouter()
router.register(r'urls', URLToScrapeViewSet, basename='url-to-scrape')
router.register(r'casas', CasaViewSet, basename='casa')
router.register(r'departamentos', DepartamentoViewSet, basename='departamento')
router.register(r'terrenos', TerrenoViewSet, basename='terreno')
router.register(r'casas-prefabricadas', CasaPrefabricadaViewSet, basename='casa-prefabricada')
router.register(r'logs', ScrapingLogViewSet, basename='scraping-log')
router.register(r'dashboard', DashboardViewSet, basename='dashboard')

urlpatterns = [
    path('', include(router.urls)),
]