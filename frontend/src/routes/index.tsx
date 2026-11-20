import { lazy } from 'react'
import { type RouteObject } from 'react-router'
import AppLayout from '@/shell/AppLayout'
import AuthLayout from '@/shell/AuthLayout'
import NotFoundPage from '@/shell/NotFoundPage'
import { ProtectedRoute, PublicOnlyRoute } from '@/features/auth/ProtectedRoute'

export const routes: RouteObject[] = [
  {
    element: <ProtectedRoute />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, Component: lazy(() => import('@/views/app/dashboard')) },
          { path: '/transactions', Component: lazy(() => import('@/views/app/transactions')) },
          { path: '/accounts', Component: lazy(() => import('@/views/app/accounts')) },
          { path: '/budget', Component: lazy(() => import('@/views/app/budget')) },
          { path: '/investments', Component: lazy(() => import('@/views/app/investments')) },
          { path: '/net-worth', Component: lazy(() => import('@/views/app/net-worth')) },
          { path: '/recurring', Component: lazy(() => import('@/views/app/recurring')) },
          { path: '/installments', Component: lazy(() => import('@/views/app/installments')) },
          { path: '/goals', Component: lazy(() => import('@/views/app/goals')) },
          { path: '/reports', Component: lazy(() => import('@/views/app/reports')) },
          { path: '/assistente', Component: lazy(() => import('@/views/app/assistant')) },
          { path: '/settings/connections', Component: lazy(() => import('@/views/app/settings/connections')) },
          { path: '/settings/categories', Component: lazy(() => import('@/views/app/settings/categories')) },
          { path: '/settings/rules', Component: lazy(() => import('@/views/app/settings/rules')) },
          { path: '/settings/alerts', Component: lazy(() => import('@/views/app/settings/alerts')) },
          { path: '/settings/preferences', Component: lazy(() => import('@/views/app/settings/preferences')) },
          { path: '/settings/ai', Component: lazy(() => import('@/views/app/settings/ai')) },
          { path: '/settings/backup', Component: lazy(() => import('@/views/app/settings/backup')) },
          { path: '/notifications', Component: lazy(() => import('@/views/app/notifications')) },
        ],
      },
      {
        element: <AuthLayout />,
        children: [
          { path: '/settings/2fa/enable', Component: lazy(() => import('@/features/auth/EnableTwoFactorPage')) },
          { path: '/settings/change-password', Component: lazy(() => import('@/features/auth/ChangePasswordPage')) },
        ],
      },
    ],
  },
  {
    element: <PublicOnlyRoute />,
    children: [
      {
        element: <AuthLayout />,
        children: [
          { path: '/setup', Component: lazy(() => import('@/features/auth/SetupPage')) },
          { path: '/login', Component: lazy(() => import('@/features/auth/LoginPage')) },
          { path: '/2fa/verify', Component: lazy(() => import('@/features/auth/TwoFactorVerifyPage')) },
        ],
      },
    ],
  },
  {
    element: <AuthLayout />,
    children: [{ path: '*', element: <NotFoundPage /> }],
  },
]
