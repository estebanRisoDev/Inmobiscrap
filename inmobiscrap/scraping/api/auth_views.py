# Crear archivo: inmobiscrap/scraping/api/auth_views.py

from rest_framework import status
from rest_framework.decorators import api_view, permission_classes
from rest_framework.permissions import AllowAny
from rest_framework.response import Response
from django.contrib.auth import authenticate, login, logout
from django.views.decorators.csrf import csrf_exempt
from django.middleware.csrf import get_token
from rest_framework.authtoken.models import Token
from django.contrib.auth.models import User

@api_view(['GET'])
@permission_classes([AllowAny])
def get_csrf_token(request):
    """Obtener token CSRF"""
    return Response({'csrfToken': get_token(request)})

@api_view(['POST'])
@permission_classes([AllowAny])
@csrf_exempt
def login_view(request):
    """Vista de login"""
    username = request.data.get('username')
    password = request.data.get('password')
    
    if not username or not password:
        return Response(
            {'error': 'Username and password required'},
            status=status.HTTP_400_BAD_REQUEST
        )
    
    # Autenticar usuario
    user = authenticate(request, username=username, password=password)
    
    if user is not None:
        # Login con sesión de Django
        login(request, user)
        
        # Crear o obtener token (opcional)
        token, created = Token.objects.get_or_create(user=user)
        
        return Response({
            'success': True,
            'username': user.username,
            'email': user.email,
            'token': token.key,
            'is_staff': user.is_staff,
            'is_superuser': user.is_superuser,
        })
    else:
        return Response(
            {'error': 'Invalid credentials'},
            status=status.HTTP_401_UNAUTHORIZED
        )

@api_view(['POST'])
def logout_view(request):
    """Vista de logout"""
    logout(request)
    return Response({'success': True})

@api_view(['GET'])
@permission_classes([AllowAny])
def check_auth(request):
    """Verificar si el usuario está autenticado"""
    if request.user.is_authenticated:
        return Response({
            'authenticated': True,
            'username': request.user.username,
            'is_staff': request.user.is_staff
        })
    else:
        return Response({
            'authenticated': False
        })