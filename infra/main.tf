data "aws_ssm_parameter" "connection_string" {
  name            = "/${var.project_name}/${var.environment}/database/execution-connection-string"
  with_decryption = true
}

resource "kubernetes_secret_v1" "database" {
  metadata {
    name      = "execution-database"
    namespace = "garage-microservices"
  }
  data = { "connection-string" = data.aws_ssm_parameter.connection_string.value }
  type = "Opaque"
}

resource "kubernetes_deployment_v1" "service" {
  metadata {
    name      = "execution-service"
    namespace = "garage-microservices"
    labels    = { app = "execution-service" }
  }
  spec {
    replicas                  = var.replicas
    progress_deadline_seconds = 300
    selector { match_labels = { app = "execution-service" } }
    strategy {
      type = "RollingUpdate"
      rolling_update {
        max_surge       = "1"
        max_unavailable = "0"
      }
    }
    template {
      metadata { labels = { app = "execution-service" } }
      spec {
        container {
          name  = "execution-service"
          image = var.container_image
          port { container_port = 8080 }
          env {
            name = "Mongo__ConnectionString"
            value_from {
              secret_key_ref {
                name = kubernetes_secret_v1.database.metadata[0].name
                key  = "connection-string"
              }
            }
          }
          readiness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 5
            period_seconds        = 5
            timeout_seconds       = 3
            failure_threshold     = 6
          }
          liveness_probe {
            http_get {
              path = "/health"
              port = 8080
            }
            initial_delay_seconds = 15
            period_seconds        = 20
            timeout_seconds       = 3
            failure_threshold     = 3
          }
        }
      }
    }
  }
}

resource "kubernetes_service_v1" "service" {
  metadata {
    name      = "execution-service"
    namespace = "garage-microservices"
  }
  spec {
    selector = { app = "execution-service" }
    port {
      port        = 8080
      target_port = 8080
    }
  }
}
