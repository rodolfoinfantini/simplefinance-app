import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring v1's exact connections lifecycle
 * endpoints (frontend/src/features/settings/connections/api.ts) — no `any`,
 * so a backend contract change surfaces here as a type error. One hook per
 * endpoint, mutations invalidate ['connections'] on success.
 */

export type ConnectionStatus = 'Updating' | 'Updated' | 'LoginError' | 'WaitingUserInput' | 'Outdated'

export type ConnectionSuggestedAction =
  | 'None'
  | 'WaitForCompletion'
  | 'ProvideMfa'
  | 'ReconnectRequired'
  | 'ReviewPartialFailure'
  | 'GenericRetry'

export interface ConnectionResponse {
  id: string
  customName: string | null
  institutionName: string | null
  connectorName: string | null
  imageUrl: string | null
  status: ConnectionStatus
  executionStatus: string | null
  statusDetail: string | null
  errorCode: string | null
  errorMessage: string | null
  lastSyncAt: string | null
  suggestedAction: ConnectionSuggestedAction
}

export interface ConnectTokenResponse {
  connectToken: string
}

export interface AccessTokenRequest {
  accessToken: string
}

export interface CreateConnectTokenRequest extends AccessTokenRequest {
  existingPluggyItemId?: string
}

export interface ReconnectTokenRequest extends AccessTokenRequest {
  /** Our internal Connection.Id (not the raw Pluggy itemId, never exposed to the frontend). */
  id: string
}

export interface RegisterConnectionRequest extends AccessTokenRequest {
  pluggyItemId: string
}

export interface RemoveConnectionRequest extends AccessTokenRequest {
  id: string
}

export interface SyncConnectionRequest extends AccessTokenRequest {
  id: string
}

export interface RenameConnectionRequest extends AccessTokenRequest {
  id: string
  /** Empty/blank clears the nickname (falls back to institution name). */
  customName: string
}

export function useConnectionsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['connections', accessToken],
    queryFn: () => apiFetch<ConnectionResponse[]>('/api/connections', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useCreateConnectTokenMutation() {
  return useMutation({
    mutationFn: ({ accessToken, existingPluggyItemId }: CreateConnectTokenRequest) =>
      apiFetch<ConnectTokenResponse>('/api/pluggy/connect-token', {
        method: 'POST',
        body: existingPluggyItemId ? { existingPluggyItemId } : {},
        accessToken,
      }),
  })
}

export function useReconnectTokenMutation() {
  return useMutation({
    mutationFn: ({ accessToken, id }: ReconnectTokenRequest) =>
      apiFetch<ConnectTokenResponse>(`/api/connections/${id}/reconnect-token`, {
        method: 'POST',
        accessToken,
      }),
  })
}

export function useRegisterConnectionMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, pluggyItemId }: RegisterConnectionRequest) =>
      apiFetch<ConnectionResponse>('/api/connections', {
        method: 'POST',
        body: { pluggyItemId },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['connections'] })
    },
  })
}

export function useRemoveConnectionMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: RemoveConnectionRequest) =>
      apiFetch<void>(`/api/connections/${id}`, {
        method: 'DELETE',
        body: { confirm: true },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['connections'] })
    },
  })
}

export function useRenameConnectionMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, customName }: RenameConnectionRequest) =>
      apiFetch<ConnectionResponse>(`/api/connections/${id}`, {
        method: 'PATCH',
        body: { customName },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['connections'] })
      void queryClient.invalidateQueries({ queryKey: ['accounts'] })
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] })
    },
  })
}

export function useSyncConnectionMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: SyncConnectionRequest) =>
      apiFetch<void>(`/api/connections/${id}/sync`, {
        method: 'POST',
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['connections'] })
    },
  })
}
